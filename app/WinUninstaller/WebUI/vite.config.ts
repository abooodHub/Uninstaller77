import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'
import { readFileSync } from 'node:fs'

// Keep the UI identity tied to the assembly, including direct dotnet builds.
const project = readFileSync(new URL('../WinUninstaller.csproj', import.meta.url), 'utf8')
const version = project.match(/<Version>(\d+\.\d+\.\d+)<\/Version>/)?.[1]
if (!version) throw new Error('The application project must declare a three-part version.')

// https://vite.dev/config/
export default defineConfig({
  base: './',
  publicDir: false,
  define: { __APP_VERSION__: JSON.stringify(version) },
  plugins: [react()],
})
