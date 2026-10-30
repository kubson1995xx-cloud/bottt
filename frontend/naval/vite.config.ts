import {defineConfig} from 'vite';
export default defineConfig({base:'/naval/',build:{outDir:'dist',emptyOutDir:true,sourcemap:false,target:'es2022',rolldownOptions:{output:{chunkFileNames:'assets/chunk-[hash].js'}}}});
