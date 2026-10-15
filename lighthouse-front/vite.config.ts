import adapter from '@sveltejs/adapter-static';
import { sveltekit } from '@sveltejs/kit/vite';
import { vitePreprocess } from '@sveltejs/vite-plugin-svelte';
import tailwindcss from '@tailwindcss/vite';
import { defineConfig, loadEnv } from 'vite';

export default defineConfig(({ mode }) => {
	const env = loadEnv(mode, process.cwd(), '');
	const allowedHosts = env.ALLOWED_HOSTS
		? env.ALLOWED_HOSTS.split(',').map((h) => h.trim())
		: [];

	return {
		plugins: [
			// SvelteKit 3 takes its configuration here; svelte.config.js is no longer supported.
			sveltekit({
				preprocess: vitePreprocess(),
				adapter: adapter({
					pages: 'dist',
					assets: 'dist',
					fallback: 'index.html',
					precompress: false,
					strict: true
				}),
				// Deprecated in Kit 3 in favour of package.json subpath imports; still supported.
				alias: {
					$lib: 'src/lib',
					$components: 'src/lib/components',
					$stores: 'src/lib/stores',
					$api: 'src/lib/api',
					$types: 'src/lib/types',
					$utils: 'src/lib/utils',
					$i18n: 'src/lib/i18n'
				}
			}),
			tailwindcss()
		],
		test: {
			environment: 'jsdom',
			globals: true,
			setupFiles: ['./vitest-setup.ts'],
			include: ['src/**/*.{test,spec}.{js,ts}'],
		},
		server: {
			...(allowedHosts.length && { allowedHosts }),
			proxy: {
				'/api': {
					target: 'http://localhost:5050',
					changeOrigin: true,
				},
			},
		},
	};
});
