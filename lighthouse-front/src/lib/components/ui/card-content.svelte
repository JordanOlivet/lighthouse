<script lang="ts">
  import { cn } from '$lib/utils';
  import type { Snippet } from 'svelte';
  import type { HTMLAttributes } from 'svelte/elements';

  interface Props extends HTMLAttributes<HTMLDivElement> {
    children?: Snippet;
    class?: string;
  }

  let { children, class: className, ...restProps }: Props = $props();
</script>

<div class={cn('p-4 pt-0 sm:p-6 sm:pt-0', className)} {...restProps}>
  {#if children}
    {@render children()}
  {/if}
</div>
