// Chỉ cache static file, bỏ qua toàn bộ _blazor/*, _framework/*, negotiate
const CACHE = 'chumchat-v2';
const STATIC = [
  '/manifest.json',
  '/icon-192.png',
  '/icon-512.png'
];

self.addEventListener('install', e => {
  e.waitUntil(
    caches.open(CACHE).then(c => c.addAll(STATIC))
  );
  self.skipWaiting();
});

self.addEventListener('activate', e => {
  e.waitUntil(
    caches.keys().then(keys => {
      return Promise.all(
        keys.map(key => {
          if (key !== CACHE) {
            return caches.delete(key);
          }
        })
      );
    }).then(() => clients.claim())
  );
});

self.addEventListener('fetch', e => {
  const url = new URL(e.request.url);
  
  // KHÔNG cache Blazor/SignalR
  if (url.pathname.startsWith('/_blazor') ||
      url.pathname.startsWith('/_framework') ||
      url.pathname.includes('negotiate') ||
      e.request.method !== 'GET') {
    return;
  }

  // Network-first cho tất cả request còn lại
  e.respondWith(
    fetch(e.request).catch(() => caches.match(e.request))
  );
});

// Lắng nghe sự kiện Push Notification từ Server gửi xuống
self.addEventListener('push', e => {
  if (!e.data) return;
  try {
    const payload = e.data.json();
    const options = {
      body: payload.message,
      icon: '/icon-192.png',
      badge: '/icon-192.png',
      data: { url: payload.url || '/' },
      vibrate: [100, 50, 100],
      actions: [
        { action: 'open', title: 'Mở Hộp Thư' }
      ]
    };
    e.waitUntil(
      self.registration.showNotification(payload.title, options)
    );
  } catch (err) {
    console.error('Lỗi phân tích push payload:', err);
  }
});

// Lắng nghe sự kiện click vào thông báo trên điện thoại
self.addEventListener('notificationclick', e => {
  e.notification.close();
  const targetUrl = new URL(e.notification.data?.url || '/', self.location.origin).href;
  
  e.waitUntil(
    clients.matchAll({ type: 'window', includeUncontrolled: true }).then(windowClients => {
      // Nếu có tab của ứng dụng đang mở thì dùng lại
      for (let i = 0; i < windowClients.length; i++) {
        let client = windowClients[i];
        if (new URL(client.url).origin === new URL(targetUrl).origin && 'focus' in client) {
          client.focus();
          if (client.url !== targetUrl && 'navigate' in client) {
            return client.navigate(targetUrl);
          }
          return;
        }
      }
      // Chưa có thì mở tab mới
      if (clients.openWindow) {
        return clients.openWindow(targetUrl);
      }
    })
  );
});
