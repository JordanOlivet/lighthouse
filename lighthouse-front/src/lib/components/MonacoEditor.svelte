<script lang="ts">
  import { onMount, onDestroy } from 'svelte';
  import { browser } from '$app/env';
  import * as theme from '$lib/stores/theme.svelte';

  interface Props {
    value?: string;
    language?: string;
    readonly?: boolean;
    onchange?: (value: string) => void;
    class?: string;
  }

  let { value = '', language = 'yaml', readonly = false, onchange, class: className = '' }: Props = $props();

  let container: HTMLDivElement;
  let editor: import('monaco-editor').editor.IStandaloneCodeEditor | null = null;
  let monaco: typeof import('monaco-editor') | null = null;

  onMount(async () => {
    if (!browser) return;

    // Dynamically import monaco-editor
    monaco = await import('monaco-editor');

    // Configure Monaco
    monaco.editor.defineTheme('custom-dark', {
      base: 'vs-dark',
      inherit: true,
      rules: [],
      colors: {
        'editor.background': '#1a1a2e',
      },
    });

    monaco.editor.defineTheme('custom-light', {
      base: 'vs',
      inherit: true,
      rules: [],
      colors: {
        'editor.background': '#ffffff',
      },
    });

    editor = monaco.editor.create(container, {
      value,
      language,
      theme: theme.isDark.current ? 'custom-dark' : 'custom-light',
      readOnly: readonly,
      minimap: { enabled: false },
      lineNumbers: 'on',
      scrollBeyondLastLine: false,
      automaticLayout: true,
      fontSize: 14,
      fontFamily: 'JetBrains Mono, Fira Code, Consolas, monospace',
      tabSize: 2,
      wordWrap: 'on',
    });

    editor.onDidChangeModelContent(() => {
      if (editor && onchange) {
        onchange(editor.getValue());
      }
    });
  });

  onDestroy(() => {
    if (editor) {
      editor.dispose();
    }
  });

  // Update theme when it changes
  $effect(() => {
    if (editor && monaco) {
      monaco.editor.setTheme(theme.isDark.current ? 'custom-dark' : 'custom-light');
    }
  });

  // Update value when it changes externally
  $effect(() => {
    if (editor && value !== editor.getValue()) {
      editor.setValue(value);
    }
  });

  // Update readonly state
  $effect(() => {
    if (editor) {
      editor.updateOptions({ readOnly: readonly });
    }
  });
</script>

<div bind:this={container} class="min-h-[400px] border border-gray-200 dark:border-gray-700 rounded-lg overflow-hidden {className}"></div>
