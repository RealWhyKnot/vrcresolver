import { createApp } from 'vue'
import { createPinia } from 'pinia'
import { MotionPlugin } from '@vueuse/motion'
import './style.css'
import 'bootstrap-icons/font/bootstrap-icons.css'
import App from './App.vue'

const app = createApp(App)
const pinia = createPinia()

app.use(pinia)
app.use(MotionPlugin)

declare global {
  interface Window {
    photino: {
      sendMessage: (message: string) => void;
      receiveMessage: (handler: (message: string) => void) => void;
      addWebMessageReceivedHandler?: (handler: (message: string) => void) => void;
    };
  }
}

const initBridge = () => {
  const win = window as any;
  const external = win.external || (win.chrome && win.chrome.webview);

  if (external && external.sendMessage && external.receiveMessage) {
    win.photino = {
      sendMessage: (msg: string) => external.sendMessage(msg),
      receiveMessage: (handler: any) => external.receiveMessage(handler)
    };
    return true;
  }
  return false;
};

if (!initBridge()) {
  const bridgeInterval = setInterval(() => {
    if (initBridge()) {
      clearInterval(bridgeInterval);
      window.dispatchEvent(new CustomEvent('photino-ready'));
    }
  }, 100);
}

app.mount('#app')
