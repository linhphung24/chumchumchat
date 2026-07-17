import express from "express";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import login from "fca-unofficial";

const PORT = process.env.PORT || 3312;
const API_KEY = process.env.API_KEY || "";
const CHUMCHAT_URL = (process.env.CHUMCHAT_URL || "http://localhost:5000").replace(/\/$/, "");

let api = null;
let loggedIn = false;

const app = express();
app.use(express.json({ limit: "2mb" }));

app.use((req, res, next) => {
    if (req.path === "/health") return next();
    if (API_KEY && req.headers["x-api-key"] !== API_KEY)
        return res.status(401).json({ error: "Sai API key" });
    next();
});

// Hàm khởi tạo login dựa trên appState
async function startLogin(appStateJson) {
    if (loggedIn && api) {
        return; // Already logged in
    }

    try {
        const appState = JSON.parse(appStateJson);
        login({ appState: appState }, (err, fcaApi) => {
            if (err) {
                console.error("Lỗi đăng nhập Messenger:", err);
                return;
            }

            api = fcaApi;
            loggedIn = true;
            console.log("[sidecar] Đăng nhập Messenger Cá Nhân thành công");

            api.setOptions({
                listenEvents: true,
                selfListen: false
            });

            startListener();
        });
    } catch (e) {
        console.error("Cookie (appState) không hợp lệ:", e);
    }
}

function startListener() {
    api.listenMqtt(async (err, message) => {
        if (err) return console.error(err);

        try {
            // Chỉ xử lý tin nhắn
            if (message.type !== "message" && message.type !== "message_reply") return;
            if (message.isGroup) return; // Bỏ qua nhóm

            let text = message.body || "";
            let attachmentUrl = null;

            if (message.attachments && message.attachments.length > 0) {
                const attach = message.attachments[0];
                if (attach.type === "photo") {
                    attachmentUrl = attach.url;
                }
            }

            const payload = {
                userId: String(message.senderID),
                name: "", // fca-unofficial không trả về tên trực tiếp trong listenMqtt, C# tự xử lý
                text,
                msgId: String(message.messageID),
                ts: Number(message.timestamp),
                attachmentUrl,
            };

            const res = await fetch(`${CHUMCHAT_URL}/webhooks/messengerpersonal`, {
                method: "POST",
                headers: { "Content-Type": "application/json", "X-Api-Key": API_KEY },
                body: JSON.stringify(payload),
            });
            if (!res.ok) console.error("[chumchat] Webhook trả lỗi", res.status, await res.text());
        } catch (e) {
            console.error("[messenger] Lỗi xử lý tin nhắn:", e);
        }
    });
}

// Khi người dùng bấm Lưu Cấu Hình, ChumChat có thể gọi một endpoint để cập nhật AppState
app.post("/login", (req, res) => {
    const { appState } = req.body;
    if (!appState) return res.status(400).json({ error: "Thiếu appState" });

    startLogin(appState);
    res.json({ ok: true });
});

app.get("/health", (req, res) => {
    res.json({ ok: true, loggedIn });
});

app.post("/send", async (req, res) => {
    const { threadId, text } = req.body || {};
    if (!loggedIn) return res.status(503).json({ error: "Chưa đăng nhập Messenger" });
    if (!threadId || !text) return res.status(400).json({ error: "Thiếu threadId hoặc text" });

    try {
        api.sendMessage(text, threadId, (err, messageInfo) => {
            if (err) return res.status(500).json({ error: String(err) });
            res.json({ ok: true, messageId: messageInfo.messageID });
        });
    } catch (err) {
        res.status(500).json({ error: String(err) });
    }
});

app.post("/send-image", async (req, res) => {
    const { threadId, url } = req.body || {};
    if (!loggedIn) return res.status(503).json({ error: "Chưa đăng nhập Messenger" });
    if (!threadId || !url) return res.status(400).json({ error: "Thiếu threadId hoặc url" });

    try {
        const imgRes = await fetch(url);
        if (!imgRes.ok) throw new Error(`Không tải được ảnh từ ${url}`);
        const ext = path.extname(new URL(url).pathname) || ".jpg";
        const tmpFile = path.join(os.tmpdir(), `chumchat-fb-${Date.now()}${ext}`);
        fs.writeFileSync(tmpFile, Buffer.from(await imgRes.arrayBuffer()));

        api.sendMessage({
            body: "",
            attachment: fs.createReadStream(tmpFile)
        }, threadId, (err, messageInfo) => {
            fs.unlinkSync(tmpFile);
            if (err) return res.status(500).json({ error: String(err) });
            res.json({ ok: true });
        });
    } catch (err) {
        res.status(500).json({ error: String(err) });
    }
});

app.post("/logout", (req, res) => {
    console.log("[sidecar] Nhận lệnh gỡ tài khoản...");
    if (api) {
        try {
            api.logout();
        } catch (e) {
            console.error("Lỗi logout API", e);
        }
    }
    loggedIn = false;
    api = null;
    res.json({ ok: true });
});

app.post("/restart", (req, res) => {
    console.log("[sidecar] Nhận lệnh khởi động lại...");
    res.json({ ok: true });
    setTimeout(() => process.exit(1), 500);
});

app.listen(PORT, () => console.log(`[sidecar] Messenger HTTP chạy tại http://localhost:${PORT}`));
