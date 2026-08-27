import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";

// GitHub Pages derives this from the repository name, so it is not a preference: the site is
// served at https://alegauss.github.io/quickshell/ and every canonical, asset path and sitemap
// entry carries the prefix. Written here and in src/routes.tsx, and nowhere else.
export const BASE = "/quickshell/";

export default defineConfig({
  base: BASE,
  plugins: [react(), tailwindcss()],
  build: {
    // docs/ is roadkeep's and never a web root, so the site builds to its own dist/.
    outDir: "dist",
    emptyOutDir: true,
  },
});
