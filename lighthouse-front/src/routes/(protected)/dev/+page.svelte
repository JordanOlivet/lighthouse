<script lang="ts">
  import { dev } from '$app/env';
  import { goto } from '$app/navigation';
  import { onMount } from 'svelte';
  import { projectUpdateState } from '$lib/stores/projectUpdate.svelte';
  import { _devFirePullProgressUpdate } from '$lib/stores/sse.svelte';
  import BulkUpdateDialog from '$lib/components/update/BulkUpdateDialog.svelte';
  import { t } from '$lib/i18n';
  import { devTestApi } from '$lib/api/devTest';
  import type { DevTestStatus, DevTestActionResult, MaintenanceDevStatus } from '$lib/api/devTest';
  import type {
    CheckAllUpdatesResponse,
    ProjectUpdateSummary,
    UpdateProgressEvent,
    ServicePullProgress,
    ProjectUpdateRequest,
  } from '$lib/types/update';

  $effect(() => {
    if (!dev) goto('/');
  });

  // --- Config state ---
  let projectCount = $state(3);
  let servicesPerProject = $state(2);
  let durationMs = $state(5000);
  let failingProjectsInput = $state('project-b');
  let dialogOpen = $state(false);

  const failingProjects = $derived(
    failingProjectsInput.split(',').map(s => s.trim()).filter(Boolean)
  );

  // --- Fake data helpers ---
  function buildFakeCheckResult(count: number, services: number): CheckAllUpdatesResponse {
    const projects: ProjectUpdateSummary[] = Array.from({ length: count }, (_, i) => ({
      projectName: `project-${String.fromCharCode(97 + i)}`,
      servicesWithUpdates: services,
      lastChecked: new Date().toISOString(),
    }));
    return {
      projects,
      projectsChecked: count,
      projectsWithUpdates: count,
      totalServicesWithUpdates: count * services,
      checkedAt: new Date().toISOString(),
    };
  }

  function buildSimSteps(projectName: string, services: number): UpdateProgressEvent[] {
    const serviceNames = Array.from({ length: services }, (_, i) => `service-${i + 1}`);

    const makeServices = (statuses: Array<{ s: string; p: number }>): ServicePullProgress[] =>
      serviceNames.map((name, i) => ({
        serviceName: name,
        status: (statuses[i]?.s ?? 'waiting') as ServicePullProgress['status'],
        progressPercent: statuses[i]?.p ?? 0,
        message: null,
      }));

    const base = { operationId: `dev-op-${projectName}`, projectName, containerId: null };

    return [
      {
        ...base,
        phase: 'pull',
        overallProgress: 0,
        services: makeServices(serviceNames.map(() => ({ s: 'waiting', p: 0 }))),
        currentLog: null,
      },
      {
        ...base,
        phase: 'pull',
        overallProgress: 20,
        services: makeServices([{ s: 'pulling', p: 10 }, ...serviceNames.slice(1).map(() => ({ s: 'waiting', p: 0 }))]),
        currentLog: `Pulling image for ${serviceNames[0]}...`,
      },
      {
        ...base,
        phase: 'pull',
        overallProgress: 40,
        services: makeServices([{ s: 'downloading', p: 60 }, ...serviceNames.slice(1).map(() => ({ s: 'waiting', p: 0 }))]),
        currentLog: `Downloading layers for ${serviceNames[0]}...`,
      },
      {
        ...base,
        phase: 'pull',
        overallProgress: 55,
        services: makeServices([{ s: 'extracting', p: 80 }, ...serviceNames.slice(1).map(() => ({ s: 'pulling', p: 5 }))]),
        currentLog: `Extracting ${serviceNames[0]}, pulling ${serviceNames[1] ?? ''}...`,
      },
      {
        ...base,
        phase: 'pull',
        overallProgress: 65,
        services: makeServices([{ s: 'pulled', p: 100 }, ...serviceNames.slice(1).map(() => ({ s: 'downloading', p: 90 }))]),
        currentLog: `${serviceNames[0]} pulled successfully`,
      },
      {
        ...base,
        phase: 'pull',
        overallProgress: 80,
        services: makeServices(serviceNames.map(() => ({ s: 'pulled', p: 100 }))),
        currentLog: 'All images pulled',
      },
      {
        ...base,
        phase: 'recreate',
        overallProgress: 90,
        services: makeServices(serviceNames.map(() => ({ s: 'recreating', p: 0 }))),
        currentLog: 'Recreating containers...',
      },
      {
        ...base,
        phase: 'recreate',
        overallProgress: 100,
        services: makeServices(serviceNames.map(() => ({ s: 'completed', p: 100 }))),
        currentLog: 'Update complete',
      },
    ];
  }

  const sleep = (ms: number) => new Promise<void>(resolve => setTimeout(resolve, ms));

  async function mockUpdateFn(
    projectName: string,
    _options: ProjectUpdateRequest
  ): Promise<void> {
    const shouldFail = failingProjects.includes(projectName);
    const steps = buildSimSteps(projectName, servicesPerProject);
    const stepDelay = Math.max(50, durationMs / steps.length);

    for (const step of steps) {
      await sleep(stepDelay);
      _devFirePullProgressUpdate(step);
    }

    if (shouldFail) {
      throw new Error(`[DEV] Simulated failure for ${projectName}`);
    }
  }

  function openDialog() {
    projectUpdateState.checkResult = buildFakeCheckResult(projectCount, servicesPerProject);
    dialogOpen = true;
  }

  function resetStore() {
    projectUpdateState.checkResult = null;
    dialogOpen = false;
  }

  const storeProjects = $derived(
    projectUpdateState.checkResult?.projects ?? []
  );

  // --- Real Docker Testing ---
  let dockerStatus = $state<DevTestStatus | null>(null);
  let dockerLoading = $state(false);
  let lastLogs = $state<string[]>([]);
  let lastError = $state<string | null>(null);

  // --- Maintenance Mode Simulation ---
  let maintenanceStatus = $state<MaintenanceDevStatus | null>(null);
  let maintenanceLoading = $state(false);
  let maintenanceDelaySeconds = $state(15);
  let maintenanceLogs = $state<string[]>([]);
  let maintenanceError = $state<string | null>(null);

  onMount(async () => {
    try {
      dockerStatus = await devTestApi.getStatus();
    } catch {
      // not critical if this fails
    }
    try {
      maintenanceStatus = await devTestApi.getMaintenanceStatus();
    } catch {
      // not critical if this fails
    }
  });

  async function dockerAction(fn: () => Promise<DevTestActionResult>) {
    dockerLoading = true;
    lastLogs = [];
    lastError = null;
    try {
      const result = await fn();
      lastLogs = result.logs;
      lastError = result.error;
    } catch (e: unknown) {
      lastError = e instanceof Error ? e.message : String(e);
    } finally {
      dockerLoading = false;
      try {
        dockerStatus = await devTestApi.getStatus();
      } catch {
        // ignore
      }
    }
  }

  async function maintenanceAction(fn: () => Promise<DevTestActionResult>) {
    maintenanceLoading = true;
    maintenanceLogs = [];
    maintenanceError = null;
    try {
      const result = await fn();
      maintenanceLogs = result.logs;
      maintenanceError = result.error;
    } catch (e: unknown) {
      maintenanceError = e instanceof Error ? e.message : String(e);
    } finally {
      maintenanceLoading = false;
      try {
        maintenanceStatus = await devTestApi.getMaintenanceStatus();
      } catch {
        // ignore
      }
    }
  }

  async function refreshMaintenanceStatus() {
    try {
      maintenanceStatus = await devTestApi.getMaintenanceStatus();
    } catch {
      // ignore
    }
  }
</script>

<div class="max-w-xl mx-auto p-8 space-y-8">
  <div>
    <h1 class="text-xl font-bold text-gray-900 dark:text-white">Bulk Update — Dev Test Page</h1>
    <p class="text-sm text-gray-500 dark:text-gray-400 mt-1">Dev-only. Not visible in production.</p>
  </div>

  <!-- Config -->
  <section class="space-y-4 p-4 border border-dashed border-gray-300 dark:border-gray-600 rounded-lg">
    <h2 class="text-sm font-semibold text-gray-700 dark:text-gray-300 uppercase tracking-wide">Configuration</h2>

    <div class="grid grid-cols-2 gap-4">
      <label class="flex flex-col gap-1">
        <span class="text-xs text-gray-600 dark:text-gray-400">Projets</span>
        <input
          type="number"
          min="1"
          max="10"
          bind:value={projectCount}
          class="border border-gray-300 dark:border-gray-600 rounded px-2 py-1 text-sm bg-white dark:bg-gray-800 text-gray-900 dark:text-white"
        />
      </label>

      <label class="flex flex-col gap-1">
        <span class="text-xs text-gray-600 dark:text-gray-400">Durée (ms)</span>
        <input
          type="number"
          min="0"
          step="500"
          bind:value={durationMs}
          class="border border-gray-300 dark:border-gray-600 rounded px-2 py-1 text-sm bg-white dark:bg-gray-800 text-gray-900 dark:text-white"
        />
      </label>
    </div>

    <label class="flex flex-col gap-1">
      <span class="text-xs text-gray-600 dark:text-gray-400">Services par projet</span>
      <input
        type="number"
        min="1"
        max="8"
        bind:value={servicesPerProject}
        class="border border-gray-300 dark:border-gray-600 rounded px-2 py-1 text-sm bg-white dark:bg-gray-800 text-gray-900 dark:text-white"
      />
    </label>

    <label class="flex flex-col gap-1">
      <span class="text-xs text-gray-600 dark:text-gray-400">Projets en échec (virgule-séparés)</span>
      <input
        type="text"
        bind:value={failingProjectsInput}
        placeholder="project-b, project-c"
        class="border border-gray-300 dark:border-gray-600 rounded px-2 py-1 text-sm bg-white dark:bg-gray-800 text-gray-900 dark:text-white"
      />
    </label>
  </section>

  <!-- Actions -->
  <div class="flex gap-3">
    <button
      onclick={openDialog}
      class="px-4 py-2 text-sm font-medium rounded-lg bg-blue-600 text-white hover:bg-blue-700 transition-colors cursor-pointer"
    >
      Ouvrir le dialog
    </button>
    <button
      onclick={resetStore}
      class="px-4 py-2 text-sm font-medium rounded-lg bg-gray-200 dark:bg-gray-700 text-gray-700 dark:text-gray-300 hover:bg-gray-300 dark:hover:bg-gray-600 transition-colors cursor-pointer"
    >
      Reset le store
    </button>
  </div>

  <!-- Store state -->
  <section class="space-y-2">
    <h2 class="text-sm font-semibold text-gray-700 dark:text-gray-300 uppercase tracking-wide">État du store</h2>
    {#if storeProjects.length === 0}
      <p class="text-sm text-gray-400 italic">Store vide</p>
    {:else}
      <ul class="space-y-1">
        {#each storeProjects as p (p.projectName)}
          <li class="text-sm text-gray-700 dark:text-gray-300">
            • <span class="font-mono font-medium">{p.projectName}</span>
            — {p.servicesWithUpdates} services
            {#if failingProjects.includes(p.projectName)}
              <span class="text-red-500 text-xs ml-1">(échec simulé)</span>
            {/if}
          </li>
        {/each}
      </ul>
    {/if}
  </section>

  <!-- Real Docker Testing -->
  <section class="space-y-4 p-4 border border-dashed border-amber-400 dark:border-amber-600 rounded-lg">
    <h2 class="text-sm font-semibold text-amber-700 dark:text-amber-400 uppercase tracking-wide">{$t('devTest.title')}</h2>

    <!-- Status -->
    {#if dockerStatus}
      <div class="space-y-1 text-sm">
        <div class="flex items-center gap-2">
          <span class={dockerStatus.filesCreated ? 'text-green-600 dark:text-green-400' : 'text-red-500'}>
            {dockerStatus.filesCreated ? '✓' : '✗'}
          </span>
          <span class="text-gray-700 dark:text-gray-300">{$t('devTest.composeFiles')}</span>
        </div>
        <div class="flex items-center gap-2">
          <span class={dockerStatus.nginxImageExists ? 'text-green-600 dark:text-green-400' : 'text-gray-400'}>
            {dockerStatus.nginxImageExists ? '✓' : '–'}
          </span>
          <span class="text-gray-700 dark:text-gray-300 font-mono">ghcr.io/nginx/nginx-unprivileged:alpine-slim</span>
        </div>
        <div class="flex items-center gap-2">
          <span class={dockerStatus.whoamiImageExists ? 'text-green-600 dark:text-green-400' : 'text-gray-400'}>
            {dockerStatus.whoamiImageExists ? '✓' : '–'}
          </span>
          <span class="text-gray-700 dark:text-gray-300 font-mono">ghcr.io/traefik/whoami:v1.11</span>
        </div>
        <p class="text-xs text-gray-500 dark:text-gray-400 font-mono pt-1">{dockerStatus.effectiveRootPath}</p>
      </div>
    {:else}
      <p class="text-sm text-gray-400 italic">{$t('devTest.statusLoading')}</p>
    {/if}

    <!-- Actions -->
    <div class="flex flex-wrap gap-2">
      <button
        onclick={() => dockerAction(devTestApi.setup)}
        disabled={dockerLoading}
        class="px-3 py-1.5 text-xs font-medium rounded-lg bg-blue-600 text-white hover:bg-blue-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors cursor-pointer"
      >
        {$t('devTest.createFiles')}
      </button>
      <button
        onclick={() => dockerAction(devTestApi.forceOutdated)}
        disabled={dockerLoading}
        title={$t('devTest.forceOutdatedHint')}
        class="px-3 py-1.5 text-xs font-medium rounded-lg bg-orange-600 text-white hover:bg-orange-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors cursor-pointer"
      >
        {$t('devTest.forceOutdated')}
      </button>
      <button
        onclick={() => dockerAction(devTestApi.restore)}
        disabled={dockerLoading}
        title={$t('devTest.restoreHint')}
        class="px-3 py-1.5 text-xs font-medium rounded-lg bg-green-600 text-white hover:bg-green-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors cursor-pointer"
      >
        {$t('devTest.restore')}
      </button>
      <button
        onclick={() => dockerAction(devTestApi.teardown)}
        disabled={dockerLoading}
        class="px-3 py-1.5 text-xs font-medium rounded-lg bg-gray-500 text-white hover:bg-gray-600 disabled:opacity-50 disabled:cursor-not-allowed transition-colors cursor-pointer"
      >
        {$t('devTest.teardown')}
      </button>
      {#if dockerLoading}
        <span class="text-xs text-gray-500 dark:text-gray-400 self-center animate-pulse">{$t('devTest.running')}</span>
      {/if}
    </div>

    <!-- Logs output -->
    {#if lastLogs.length > 0 || lastError}
      <div class="space-y-1">
        <p class="text-xs font-semibold text-gray-600 dark:text-gray-400 uppercase tracking-wide">{$t('devTest.lastOutput')}</p>
        {#if lastError}
          <p class="text-xs text-red-500">{lastError}</p>
        {/if}
        <pre class="text-xs font-mono bg-gray-100 dark:bg-gray-800 text-gray-800 dark:text-gray-200 rounded p-3 overflow-y-auto max-h-48 whitespace-pre-wrap">{lastLogs.join('\n')}</pre>
      </div>
    {/if}

    <!-- Hint -->
    <p class="text-xs text-amber-700 dark:text-amber-400 border-t border-amber-200 dark:border-amber-800 pt-3">
      {$t('devTest.hint')}
    </p>
  </section>

  <!-- Maintenance Mode Simulation -->
  <section class="space-y-4 p-4 border border-dashed border-purple-400 dark:border-purple-600 rounded-lg">
    <h2 class="text-sm font-semibold text-purple-700 dark:text-purple-400 uppercase tracking-wide">
      Maintenance Mode Simulation
    </h2>

    <!-- Status -->
    {#if maintenanceStatus}
      <div class="space-y-1 text-sm">
        <div class="flex items-center gap-2">
          <span class="text-gray-600 dark:text-gray-400">Instance ID:</span>
          <span class="font-mono text-gray-900 dark:text-white">{maintenanceStatus.instanceId}</span>
          <button
            onclick={refreshMaintenanceStatus}
            class="text-xs text-purple-600 dark:text-purple-400 hover:underline cursor-pointer"
          >
            refresh
          </button>
        </div>
        <div class="flex items-center gap-2">
          <span class={maintenanceStatus.isReady ? 'text-green-600 dark:text-green-400' : 'text-yellow-500'}>
            {maintenanceStatus.isReady ? '✓ Ready' : '⏳ Not Ready'}
          </span>
        </div>
        <div class="flex items-center gap-2">
          <span class="text-gray-600 dark:text-gray-400">Uptime:</span>
          <span class="text-gray-900 dark:text-white">{Math.round(maintenanceStatus.uptimeSeconds)}s</span>
        </div>
      </div>
    {:else}
      <p class="text-sm text-gray-400 italic">Loading status...</p>
    {/if}

    <!-- Configuration -->
    <div class="flex items-center gap-3">
      <label class="flex items-center gap-2">
        <span class="text-xs text-gray-600 dark:text-gray-400">Delay (seconds):</span>
        <input
          type="number"
          min="5"
          max="60"
          bind:value={maintenanceDelaySeconds}
          class="w-16 border border-gray-300 dark:border-gray-600 rounded px-2 py-1 text-sm bg-white dark:bg-gray-800 text-gray-900 dark:text-white"
        />
      </label>
    </div>

    <!-- Actions -->
    <div class="flex flex-wrap gap-2">
      <button
        onclick={() => maintenanceAction(() => devTestApi.simulateMaintenance(maintenanceDelaySeconds))}
        disabled={maintenanceLoading}
        class="px-3 py-1.5 text-xs font-medium rounded-lg bg-purple-600 text-white hover:bg-purple-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors cursor-pointer"
      >
        Launch Simulated Maintenance
      </button>
      <button
        onclick={() => maintenanceAction(devTestApi.resetInstance)}
        disabled={maintenanceLoading}
        class="px-3 py-1.5 text-xs font-medium rounded-lg bg-gray-600 text-white hover:bg-gray-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors cursor-pointer"
      >
        Reset Instance
      </button>
      <button
        onclick={() => maintenanceAction(() => devTestApi.setReady(false))}
        disabled={maintenanceLoading}
        class="px-3 py-1.5 text-xs font-medium rounded-lg bg-yellow-600 text-white hover:bg-yellow-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors cursor-pointer"
      >
        Set Not Ready
      </button>
      <button
        onclick={() => maintenanceAction(() => devTestApi.setReady(true))}
        disabled={maintenanceLoading}
        class="px-3 py-1.5 text-xs font-medium rounded-lg bg-green-600 text-white hover:bg-green-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors cursor-pointer"
      >
        Set Ready
      </button>
      {#if maintenanceLoading}
        <span class="text-xs text-gray-500 dark:text-gray-400 self-center animate-pulse">Running...</span>
      {/if}
    </div>

    <!-- Logs output -->
    {#if maintenanceLogs.length > 0 || maintenanceError}
      <div class="space-y-1">
        <p class="text-xs font-semibold text-gray-600 dark:text-gray-400 uppercase tracking-wide">Output</p>
        {#if maintenanceError}
          <p class="text-xs text-red-500">{maintenanceError}</p>
        {/if}
        <pre class="text-xs font-mono bg-gray-100 dark:bg-gray-800 text-gray-800 dark:text-gray-200 rounded p-3 overflow-y-auto max-h-48 whitespace-pre-wrap">{maintenanceLogs.join('\n')}</pre>
      </div>
    {/if}

    <!-- Hint -->
    <p class="text-xs text-purple-700 dark:text-purple-400 border-t border-purple-200 dark:border-purple-800 pt-3">
      This section simulates the maintenance mode cycle without triggering a real update.
      "Launch Simulated Maintenance" broadcasts a MaintenanceMode SSE event, waits for the delay,
      then resets the instance ID to simulate a container restart.
    </p>
  </section>
</div>

<BulkUpdateDialog
  open={dialogOpen}
  onClose={() => { dialogOpen = false; }}
  updateFn={mockUpdateFn}
/>
