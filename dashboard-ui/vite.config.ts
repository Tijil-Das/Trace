import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// `base: './'` matters: the WPF shell serves these files from a WebView2 virtual
// host (or straight off disk), so every asset reference has to stay relative.
export default defineConfig({
  base: './',
  plugins: [react()],
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    target: 'es2022',
    assetsDir: 'assets',
    sourcemap: false,
    chunkSizeWarningLimit: 900,
  },
  server: {
    port: 5173,
    strictPort: false,
    open: false,
  },
  preview: {
    port: 4173,
    strictPort: false,
  },
});
