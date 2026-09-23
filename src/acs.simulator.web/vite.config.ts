import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import path from 'path';

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
    },
  },
  server: {
    port: 3001,
    proxy: {
      '/api/simulator': {
        target: 'http://localhost:9060',
        changeOrigin: true,
      },
      '/hubs/simulator': {
        target: 'http://localhost:9060',
        changeOrigin: true,
        ws: true,
      },
    },
  },
});
