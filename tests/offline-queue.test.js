const fs = require('fs');
const vm = require('vm');
const source = fs.readFileSync('src/Gbrindes.Web/wwwroot/js/offline-queue.js', 'utf8');
const storage = new Map([['gbrindes.auth.token', 'restored-token']]);
const localStorage = {
  getItem: key => storage.get(key) || null,
  setItem: (key, value) => storage.set(key, String(value)),
  removeItem: key => storage.delete(key)
};
const window = { addEventListener() {}, dispatchEvent() {}, localStorage };
const context = { window, localStorage, console, FormData: class {}, CustomEvent: class {}, crypto: { randomUUID: () => 'test-id' } };
vm.runInNewContext(source, context);
const queue = window.OfflineQueue;
const multipart = {
  __multipart: true,
  fields: { item_id: 'item-1', quantity: '2' },
  file: { field: 'attachment', name: 'nota.pdf', type: 'application/pdf', dataBase64: 'JVBERi0xLjQ=' }
};
if (!queue.canQueue('POST', '/api/v1/stock/exits', multipart, 'offline-key-01')) {
  throw new Error('multipart attachment should be queueable');
}
if (!queue.canQueue('POST', '/api/v1/items/item-1/entry', multipart, 'offline-key-01')) {
  throw new Error('item entry multipart should be queueable');
}
if (queue.canQueue('POST', '/api/v1/stock/exits', { __multipart: true }, 'offline-key-01')) {
  throw new Error('multipart without file must not be queueable');
}
console.log('offline queue contract: PASS');
