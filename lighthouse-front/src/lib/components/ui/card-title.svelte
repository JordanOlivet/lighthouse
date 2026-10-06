<script lang="ts">
  import { cn } from '$lib/utils';
  import type { Snippet } from 'svelte';
  import type { HTMLAttributes } from 'svelte/elements';

  interface Props extends HTMLAttributes<HTMLHeadingElement> {
    children?: Snippet;
    class?: string;
  }

  let { children, class: className, ...restProps }: Props = $props();
</script>

<h3 class={cn('text-base font-semibold leading-tight tracking-tight sm:text-2xl sm:leading-none', className)} {...restProps}>
  {#if children}
    {@render children()}
  {/if}
</h3>
