// Sidecar Zalo cá nhân cho ChumChat — dùng thư viện zca-js (KHÔNG chính thức).
// ⚠ Zalo có thể khóa tài khoản nếu phát hiện — cân nhắc dùng tài khoản phụ.
//
// Luồng hoạt động:
//   1. Đăng nhập Zalo Web: lần đầu quét QR (ảnh lưu tại ./qr.png), các lần sau
//      tự đăng nhập lại bằng credentials đã lưu (./credentials.json).
//   2. Tin nhắn đến → POST về ChumChat: {CHUMCHAT_URL}/webhooks/zalopersonal
//   3. ChumChat gửi trả lời qua HTTP: POST /send (văn bản), POST /send-image (ảnh)
//
// Biến môi trường:
//   PORT          cổng HTTP của sidecar (mặc định 3311)
//   API_KEY       chuỗi bí mật, phải trùng với ô "API Key" trong trang Cấu hình ChumChat
//   CHUMCHAT_URL  địa chỉ app ChumChat (mặc định http://localhost:5000)

import express from "express";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import sharp from "sharp";
import { Zalo, ThreadType } from "zca-js";

const PORT = process.env.PORT || 3311;
const API_KEY = process.env.API_KEY || "";
const CHUMCHAT_URL = (process.env.CHUMCHAT_URL || "http://localhost:5000").replace(/\/$/, "");
const CREDENTIALS_FILE = new URL("./credentials.json", import.meta.url).pathname;

let api = null;
let loggedIn = false;
let currentQr = null; // data URL của mã QR đang chờ quét (null khi đã đăng nhập)

const zalo = new Zalo({
    selfListen: false, // không nhận lại tin do chính mình gửi
    checkUpdate: false,
    // zca-js v2 yêu cầu tự cung cấp hàm đọc kích thước ảnh
    imageMetadataGetter: async (filePath) => {
        const meta = await sharp(filePath).metadata();
        return {
            width: meta.width,
            height: meta.height,
            size: fs.statSync(filePath).size,
        };
    },
});

async function login() {
    // Thử đăng nhập lại bằng credentials đã lưu
    if (fs.existsSync(CREDENTIALS_FILE)) {
        try {
            const saved = JSON.parse(fs.readFileSync(CREDENTIALS_FILE, "utf8"));
            api = await zalo.login(saved);
            console.log("[zalo] Đăng nhập lại bằng credentials đã lưu — OK");
            return;
        } catch (err) {
            console.warn("[zalo] Credentials cũ hết hạn, chuyển sang quét QR:", err.message);
        }
    }

    // Đăng nhập bằng QR: giữ mã QR trong bộ nhớ (dạng base64) để hiện trong trình duyệt ChumChat,
    // đồng thời vẫn lưu ra file qr.png dự phòng.
    api = await zalo.loginQR({ qrPath: "./qr.png" }, (event) => {
        try {
            let b64 = event?.data?.image; // một số phiên bản trả base64 trong event
            if (b64) {
                fs.writeFileSync("./qr.png", Buffer.from(b64, "base64"));
            } else if (fs.existsSync("./qr.png")) {
                b64 = fs.readFileSync("./qr.png").toString("base64");
            }
            if (b64) {
                currentQr = "data:image/png;base64," + b64;
                console.log("[zalo] Có mã QR mới — mở trang Cấu hình > Zalo cá nhân > 'Hiện mã QR' để quét");
            }
        } catch (e) {
            console.warn("[zalo] Không đọc được mã QR:", e.message);
        }
    });
    currentQr = null; // đăng nhập xong thì xóa QR

    // Lưu credentials để lần sau không phải quét lại
    try {
        const ctx = api.getContext();
        fs.writeFileSync(CREDENTIALS_FILE, JSON.stringify({
            cookie: ctx.cookie?.toJSON ? ctx.cookie.toJSON() : ctx.cookie,
            imei: ctx.imei,
            userAgent: ctx.userAgent,
        }, null, 2));
        console.log("[zalo] Đã lưu credentials vào credentials.json");
    } catch (err) {
        console.warn("[zalo] Không lưu được credentials (lần sau phải quét QR lại):", err.message);
    }
}

function getThreadType(threadId) {
    const s = String(threadId);
    if (s.startsWith("g") || s.length > 15) return ThreadType.Group;
    return ThreadType.User;
}

function startListener() {
    api.listener.on("message", async (message) => {
        try {
            // Hỗ trợ cả 1-1 (User) và Nhóm (Group)
            if (message.type !== ThreadType.User && message.type !== ThreadType.Group) return;

            const data = message.data || {};
            let text = "";
            let attachmentUrl = null;

            if (typeof data.content === "string") {
                text = data.content;
            } else if (data.content && typeof data.content === "object") {
                // Tin ảnh/đính kèm/sticker: content là object có href/thumb/url
                attachmentUrl = data.content.href || data.content.thumb || data.content.spriteUrl || data.content.url || null;
                text = data.content.title || (data.content.id ? "[Sticker Zalo]" : "");
                if (!attachmentUrl && !text) text = "[Sticker Zalo]";
            } else if (data.property && data.property.stickerId) {
                text = "[Sticker Zalo]";
            } else {
                return;
            }

            const isGroup = message.type === ThreadType.Group;
            const senderName = data.dName || "Thành viên";
            const groupName = data.gName || data.groupName || (data.grid ? `Nhóm Zalo ${data.grid}` : "Nhóm Zalo");

            // Với tin nhắn nhóm từ thành viên khác, thêm tiền tố [Tên người gửi]:
            let formattedText = text;
            if (isGroup && !message.isSelf && senderName) {
                formattedText = `${senderName}: ${text}`;
            }

            const payload = {
                userId: String(message.threadId),
                name: isGroup ? `[Nhóm] ${groupName}` : (data.dName || ""),
                text: formattedText,
                msgId: data.msgId ? String(data.msgId) : null,
                ts: data.ts ? Number(data.ts) : Date.now(),
                attachmentUrl,
                isOutbound: Boolean(message.isSelf),
            };

            const res = await fetch(`${CHUMCHAT_URL}/webhooks/zalopersonal`, {
                method: "POST",
                headers: { "Content-Type": "application/json", "X-Api-Key": API_KEY },
                body: JSON.stringify(payload),
            });
            if (!res.ok) console.error("[chumchat] Webhook trả lỗi", res.status, await res.text());
        } catch (err) {
            console.error("[zalo] Lỗi xử lý tin nhắn đến:", err);
        }
    });

    api.listener.on("error", (err) => console.error("[zalo] Listener lỗi:", err));
    api.listener.start();
    loggedIn = true;
    console.log(`[sidecar] Đang nghe tin nhắn Zalo (Cá nhân & Nhóm), đẩy về ${CHUMCHAT_URL}/webhooks/zalopersonal`);
}

// ===== HTTP server nhận lệnh gửi tin từ ChumChat =====

const app = express();
app.use(express.json({ limit: "2mb" }));

app.use((req, res, next) => {
    if (req.path === "/health") return next();
    if (API_KEY && req.headers["x-api-key"] !== API_KEY)
        return res.status(401).json({ error: "Sai API key" });
    next();
});

app.get("/health", (req, res) => {
    let qr = currentQr;
    if (!loggedIn && !qr && fs.existsSync("./qr.png")) {
        try {
            qr = "data:image/png;base64," + fs.readFileSync("./qr.png").toString("base64");
        } catch (e) {
            console.warn("Lỗi đọc file qr.png:", e.message);
        }
    }
    let userId = null;
    if (loggedIn && api) {
        try {
            const ctx = api.getContext();
            userId = ctx?.uid || ctx?.userId || null;
        } catch (e) {}
    }
    res.json({ ok: true, loggedIn, qr: loggedIn ? null : qr, userId });
});

app.get("/stickers", async (req, res) => {
    const keyword = req.query.keyword || "hello";
    if (!loggedIn) return res.status(503).json({ error: "Chưa đăng nhập Zalo" });
    try {
        const stickerIds = await api.getStickers(keyword);
        if (!stickerIds || stickerIds.length === 0) return res.json({ stickers: [] });
        const details = await Promise.all(
            stickerIds.slice(0, 15).map(id => api.getStickersDetail(id).catch(() => null))
        );
        res.json({ stickers: details.filter(Boolean) });
    } catch (err) {
        console.error("[zalo] Lỗi lấy sticker:", err);
        res.status(500).json({ error: String(err.message || err) });
    }
});

app.post("/send-sticker", async (req, res) => {
    const { threadId, keyword, stickerId } = req.body || {};
    if (!loggedIn) return res.status(503).json({ error: "Chưa đăng nhập Zalo (quét QR trong log sidecar)" });
    if (!threadId) return res.status(400).json({ error: "Thiếu threadId" });

    try {
        const type = getThreadType(threadId);
        let targetStickerId = stickerId;

        if (!targetStickerId && keyword) {
            const stickerIds = await api.getStickers(keyword);
            if (stickerIds && stickerIds.length > 0) {
                targetStickerId = stickerIds[0];
            }
        }

        if (!targetStickerId) {
            return res.status(404).json({ error: "Không tìm thấy sticker phù hợp" });
        }

        const stickerObject = await api.getStickersDetail(targetStickerId);
        await api.sendMessageSticker(stickerObject, String(threadId), type);
        res.json({ ok: true });
    } catch (err) {
        console.error("[zalo] Gửi sticker lỗi:", err);
        res.status(500).json({ error: String(err.message || err) });
    }
});

app.post("/send", async (req, res) => {
    const { threadId, text } = req.body || {};
    if (!loggedIn) return res.status(503).json({ error: "Chưa đăng nhập Zalo (quét QR trong log sidecar)" });
    if (!threadId || !text) return res.status(400).json({ error: "Thiếu threadId hoặc text" });
    try {
        const type = getThreadType(threadId);
        await api.sendMessage({ msg: text }, String(threadId), type);
        res.json({ ok: true });
    } catch (err) {
        console.error("[zalo] Gửi tin lỗi:", err);
        res.status(500).json({ error: String(err.message || err) });
    }
});

app.post("/send-image", async (req, res) => {
    const { threadId, url } = req.body || {};
    if (!loggedIn) return res.status(503).json({ error: "Chưa đăng nhập Zalo (quét QR trong log sidecar)" });
    if (!threadId || !url) return res.status(400).json({ error: "Thiếu threadId hoặc url" });
    try {
        // Tải ảnh về file tạm rồi gửi dưới dạng đính kèm
        const imgRes = await fetch(url);
        if (!imgRes.ok) throw new Error(`Không tải được ảnh từ ${url} (HTTP ${imgRes.status})`);
        const ext = path.extname(new URL(url).pathname) || ".jpg";
        const tmpFile = path.join(os.tmpdir(), `chumchat-${Date.now()}${ext}`);
        fs.writeFileSync(tmpFile, Buffer.from(await imgRes.arrayBuffer()));
        try {
            const type = getThreadType(threadId);
            await api.sendMessage({ msg: "", attachments: [tmpFile] }, String(threadId), type);
        } finally {
            fs.unlinkSync(tmpFile);
        }
        res.json({ ok: true });
    } catch (err) {
        console.error("[zalo] Gửi ảnh lỗi:", err);
        res.status(500).json({ error: String(err.message || err) });
    }
});

app.post("/send-file", async (req, res) => {
    const { threadId, url, fileName } = req.body || {};
    if (!loggedIn) return res.status(503).json({ error: "Chưa đăng nhập Zalo (quét QR trong log sidecar)" });
    if (!threadId || !url) return res.status(400).json({ error: "Thiếu threadId hoặc url" });
    try {
        const fileRes = await fetch(url);
        if (!fileRes.ok) throw new Error(`Không tải được file từ ${url} (HTTP ${fileRes.status})`);
        const ext = path.extname(fileName || new URL(url).pathname) || ".pdf";
        const tmpFile = path.join(os.tmpdir(), `chumchat-${Date.now()}${ext}`);
        fs.writeFileSync(tmpFile, Buffer.from(await fileRes.arrayBuffer()));
        try {
            const type = getThreadType(threadId);
            await api.sendMessage({ msg: "", attachments: [tmpFile] }, String(threadId), type);
        } finally {
            fs.unlinkSync(tmpFile);
        }
        res.json({ ok: true });
    } catch (err) {
        console.error("[zalo] Gửi file lỗi:", err);
        res.status(500).json({ error: String(err.message || err) });
    }
});
app.post("/logout", (req, res) => {
    console.log("[sidecar] Nhận lệnh gỡ tài khoản, đang xóa credentials...");
    try {
        if (fs.existsSync(CREDENTIALS_FILE)) fs.unlinkSync(CREDENTIALS_FILE);
        if (fs.existsSync("./qr.png")) fs.unlinkSync("./qr.png");
    } catch (e) {
        console.warn("Lỗi khi xóa credentials:", e.message);
    }
    res.json({ ok: true });
    setTimeout(() => process.exit(1), 500); // Thoát để PM2/Systemd tự khởi động lại
});

app.post("/restart", (req, res) => {
    console.log("[sidecar] Nhận lệnh khởi động lại để làm mới QR...");
    res.json({ ok: true });
    setTimeout(() => process.exit(1), 500); // Thoát để PM2/Systemd tự khởi động lại
});

app.listen(PORT, () => console.log(`[sidecar] HTTP chạy tại http://localhost:${PORT}`));

login()
    .then(startListener)
    .catch((err) => {
        console.error("[zalo] Đăng nhập thất bại (có thể do hết hạn QR):", err);
        console.error("Sidecar sẽ tự thoát để khởi động lại và tạo mã QR mới...");
        process.exit(1);
    });
