<script lang="ts">
  import { onMount } from 'svelte';
  import { createQuery, createMutation, useQueryClient } from '@tanstack/svelte-query';
  import { Settings, RefreshCw, Download, CheckCircle, ExternalLink, AlertTriangle, Check, X, Bell, Send, FolderOpen } from 'lucide-svelte';
  import { updateApi } from '$lib/api/update';
  import configApi from '$lib/api/config';
  import { composeApi } from '$lib/api/compose';
  import ConfirmDialog from '$lib/components/common/ConfirmDialog.svelte';
  import FilePicker from '$lib/components/common/FilePicker.svelte';
  import ChangelogDisplay from '$lib/components/update/ChangelogDisplay.svelte';
  import RegistryManagement from '$lib/components/registry/RegistryManagement.svelte';
  import Button from '$lib/components/ui/button.svelte';
  import Card from '$lib/components/ui/card.svelte';
  import CardHeader from '$lib/components/ui/card-header.svelte';
  import CardTitle from '$lib/components/ui/card-title.svelte';
  import CardContent from '$lib/components/ui/card-content.svelte';
  import Badge from '$lib/components/ui/badge.svelte';
  import Tabs from '$lib/components/ui/tabs.svelte';
  import TabsList from '$lib/components/ui/tabs-list.svelte';
  import TabsTrigger from '$lib/components/ui/tabs-trigger.svelte';
  import TabsContent from '$lib/components/ui/tabs-content.svelte';
  import { t, locale } from '$lib/i18n';
  import { toast } from 'svelte-sonner';
  import { isAdmin } from '$lib/stores/auth.svelte';
  import { updateState, checkForUpdates } from '$lib/stores/update.svelte';
  import { projectUpdateState, saveIntervalToSettings } from '$lib/stores/projectUpdate.svelte';
  import {
    autoUpdateState,
    loadAutoUpdateSettings,
    saveAutoUpdateSetting,
    AUTO_UPDATE_KEYS
  } from '$lib/stores/autoUpdate.svelte';
  import {
    autoPruneState,
    loadAutoPruneSettings,
    AUTO_PRUNE_KEYS
  } from '$lib/stores/autoPrune.svelte';
  import {
    notificationsState,
    loadNotificationSettings,
    saveNotificationSetting,
    testDiscordWebhook,
    NOTIF_KEYS
  } from '$lib/stores/notifications.svelte';
  import { validateCron, formatNextRun, formatCountdown } from '$lib/utils/cron';

  const queryClient = useQueryClient();

  // Tab state
  let activeTab = $state('general');

  // Log level (General tab)
  const logLevelQuery = createQuery(() => ({
    queryKey: ['log-level'],
    queryFn: () => configApi.getLogLevel(),
  }));

  const updateLogLevelMutation = createMutation(() => ({
    mutationFn: (value: string) => configApi.updateLogLevel(value),
    onSuccess: (data) => {
      queryClient.setQueryData(['log-level'], data);
      toast.success($t('settings.general.logLevelSaved'));
    },
    onError: () => {
      toast.error($t('errors.generic'));
      queryClient.invalidateQueries({ queryKey: ['log-level'] });
    },
  }));

  function handleLogLevelChange(e: Event) {
    const select = e.target as HTMLSelectElement;
    updateLogLevelMutation.mutate(select.value);
  }

  // Update-related state
  let updateConfirmDialog = $state({ open: false });

  // Project update check interval options (in minutes)
  const intervalOptions = [
    { value: 15, label: '15 min' },
    { value: 30, label: '30 min' },
    { value: 60, label: '1 hour' },
    { value: 120, label: '2 hours' },
    { value: 360, label: '6 hours' },
    { value: 720, label: '12 hours' },
    { value: 1440, label: '24 hours' },
  ];

  let isSavingInterval = $state(false);

  async function handleIntervalChange(e: Event) {
    const select = e.target as HTMLSelectElement;
    const newInterval = parseInt(select.value, 10);

    isSavingInterval = true;
    try {
      const success = await saveIntervalToSettings(newInterval);
      if (success) {
        toast.success($t('settings.intervalSaved'));
      } else {
        toast.error($t('errors.generic'));
      }
    } catch {
      toast.error($t('errors.generic'));
    } finally {
      isSavingInterval = false;
    }
  }

  // Trigger update mutation
  const triggerUpdateMutation = createMutation(() => ({
    mutationFn: () => updateApi.triggerAppUpdate(),
    onSuccess: (data) => {
      if (data.success) {
        toast.success($t('update.updateStarted'));
        updateConfirmDialog.open = false;
        // The SignalR MaintenanceMode event will trigger the overlay
      } else {
        toast.error(data.message || $t('update.updateFailed'));
      }
    },
    onError: (error: Error) => {
      toast.error($t('update.updateFailed'));
    },
  }));

  async function handleCheckUpdate() {
    const result = await checkForUpdates(true); // Force check
    if (result) {
      if (result.updateAvailable) {
        toast.success($t('update.updateAvailable'));
      } else {
        toast.success($t('update.upToDate'));
      }
    } else if (updateState.checkError) {
      toast.error($t('update.checkFailed'));
    }
  }

  function handleUpdateNow() {
    updateConfirmDialog.open = true;
  }

  function confirmUpdate() {
    triggerUpdateMutation.mutate();
  }

  function formatLastChecked(date: Date | null): string {
    if (!date) return $t('update.never');
    return date.toLocaleString();
  }

  function formatVersionDate(dateStr: string | null | undefined): string | null {
    if (!dateStr) return null;
    try {
      const date = new Date(dateStr);
      const day = date.getDate().toString().padStart(2, '0');
      const month = (date.getMonth() + 1).toString().padStart(2, '0');
      const year = date.getFullYear();
      const hours = date.getHours().toString().padStart(2, '0');
      const minutes = date.getMinutes().toString().padStart(2, '0');
      const seconds = date.getSeconds().toString().padStart(2, '0');
      return `${day}/${month}/${year} ${hours}:${minutes}:${seconds}`;
    } catch {
      return null;
    }
  }

  // Get the appropriate dates based on version type
  let currentVersionDate = $derived.by(() => {
    if (!updateState.updateInfo) return null;
    if (updateState.updateInfo.isDevVersion) {
      return formatVersionDate(updateState.updateInfo.localCreatedAt);
    }
    return formatVersionDate(updateState.updateInfo.currentVersionPublishedAt);
  });

  let latestVersionDate = $derived.by(() => {
    if (!updateState.updateInfo) return null;
    if (updateState.updateInfo.isDevVersion) {
      return formatVersionDate(updateState.updateInfo.remoteCreatedAt);
    }
    return formatVersionDate(updateState.updateInfo.latestVersionPublishedAt);
  });

  // Auto-update state
  let savingKey = $state<string | null>(null);

  let cronstrueLocale = $derived(
    $locale === 'fr' ? 'fr' : $locale === 'es' ? 'es' : 'en'
  );

  let composeCronValidation = $derived(validateCron(autoUpdateState.composeCron, cronstrueLocale));
  let appCronValidation = $derived(validateCron(autoUpdateState.appCron, cronstrueLocale));

  // Live clock to drive the "time remaining" countdown (ticks every 30s)
  let now = $state(new Date());
  let countdownLabels = $derived({
    day: $t('settings.autoUpdate.countdownDay'),
    hour: $t('settings.autoUpdate.countdownHour'),
    minute: $t('settings.autoUpdate.countdownMinute'),
    soon: $t('settings.autoUpdate.countdownSoon')
  });
  let composeCountdown = $derived(
    formatCountdown(composeCronValidation.nextRun, countdownLabels, now)
  );
  let appCountdown = $derived(formatCountdown(appCronValidation.nextRun, countdownLabels, now));

  let autoPruneCronValidation = $derived(validateCron(autoPruneState.cron, cronstrueLocale));
  let autoPruneCountdown = $derived(
    formatCountdown(autoPruneCronValidation.nextRun, countdownLabels, now)
  );

  // Discord notification state
  // Webhook URL is write-only: the API returns it masked, so we only persist a
  // new value once the user actually edits the field.
  let webhookDirty = $state(false);
  let testingWebhook = $state(false);

  // Global compose env file (General tab)
  const COMPOSE_GLOBAL_ENV_FILE_KEY = 'ComposeGlobalEnvFile';
  let globalEnvFile = $state('');
  let lastSavedEnvFile = $state('');
  let savingGlobalEnvFile = $state(false);
  let showEnvFilePicker = $state(false);
  // Root the picker at a managed compose folder so the user lands where their compose files live.
  let envPickerInitialPath = $state('');

  async function loadGlobalEnvFile() {
    try {
      const settings = await configApi.getSettings();
      globalEnvFile = settings[COMPOSE_GLOBAL_ENV_FILE_KEY] ?? '';
      lastSavedEnvFile = globalEnvFile;
    } catch {
      // Non-blocking: leave the field empty if settings cannot be loaded.
    }
  }

  async function loadEnvPickerInitialPath() {
    try {
      const health = await composeApi.getComposeHealth();
      if (health.composeDiscovery.accessible) {
        envPickerInitialPath = health.composeDiscovery.rootPath;
      }
    } catch {
      // Non-blocking: the picker just opens at the filesystem root instead.
    }
  }

  async function saveGlobalEnvFile() {
    savingGlobalEnvFile = true;
    try {
      const value = globalEnvFile.trim();
      await configApi.updateSetting(COMPOSE_GLOBAL_ENV_FILE_KEY, { value });
      lastSavedEnvFile = value;
      globalEnvFile = value;
      toast.success($t('settings.composeEnv.saved'));
    } catch {
      toast.error($t('settings.composeEnv.saveFailed'));
    } finally {
      savingGlobalEnvFile = false;
    }
  }

  // Persist manual edits when the field loses focus (only when actually changed).
  function handleEnvFileBlur() {
    if (globalEnvFile.trim() !== lastSavedEnvFile) {
      void saveGlobalEnvFile();
    }
  }

  function handleEnvFileSelected(path: string) {
    globalEnvFile = path;
    showEnvFilePicker = false;
    void saveGlobalEnvFile();
  }

  onMount(() => {
    void loadAutoUpdateSettings();
    void loadAutoPruneSettings();
    void loadNotificationSettings();
    void loadGlobalEnvFile();
    void loadEnvPickerInitialPath();
    const timer = setInterval(() => {
      now = new Date();
    }, 30000);
    return () => clearInterval(timer);
  });

  async function persistSetting(key: string, value: string) {
    savingKey = key;
    const result = await saveAutoUpdateSetting(key, value);
    savingKey = null;
    if (result.ok) {
      toast.success($t('settings.autoUpdate.saved'));
    } else {
      toast.error(result.error || $t('settings.autoUpdate.saveFailed'));
    }
    return result.ok;
  }

  async function toggleComposeEnabled(e: Event) {
    const checked = (e.target as HTMLInputElement).checked;
    const previous = autoUpdateState.composeEnabled;
    autoUpdateState.composeEnabled = checked;
    const ok = await persistSetting(AUTO_UPDATE_KEYS.composeEnabled, checked ? 'true' : 'false');
    if (!ok) autoUpdateState.composeEnabled = previous;
  }

  async function toggleAppEnabled(e: Event) {
    const checked = (e.target as HTMLInputElement).checked;
    const previous = autoUpdateState.appEnabled;
    autoUpdateState.appEnabled = checked;
    const ok = await persistSetting(AUTO_UPDATE_KEYS.appEnabled, checked ? 'true' : 'false');
    if (!ok) autoUpdateState.appEnabled = previous;
  }

  function onComposeCronInput(e: Event) {
    autoUpdateState.composeCron = (e.target as HTMLInputElement).value;
  }

  function onAppCronInput(e: Event) {
    autoUpdateState.appCron = (e.target as HTMLInputElement).value;
  }

  async function commitComposeCron() {
    if (!composeCronValidation.valid) return;
    await persistSetting(AUTO_UPDATE_KEYS.composeCron, autoUpdateState.composeCron.trim());
  }

  async function commitAppCron() {
    if (!appCronValidation.valid) return;
    await persistSetting(AUTO_UPDATE_KEYS.appCron, autoUpdateState.appCron.trim());
  }

  async function toggleAutoPruneEnabled(e: Event) {
    const checked = (e.target as HTMLInputElement).checked;
    const previous = autoPruneState.enabled;
    autoPruneState.enabled = checked;
    const ok = await persistSetting(AUTO_PRUNE_KEYS.enabled, checked ? 'true' : 'false');
    if (!ok) autoPruneState.enabled = previous;
  }

  async function toggleAutoPruneDanglingOnly(e: Event) {
    const checked = (e.target as HTMLInputElement).checked;
    const previous = autoPruneState.danglingOnly;
    autoPruneState.danglingOnly = checked;
    const ok = await persistSetting(AUTO_PRUNE_KEYS.danglingOnly, checked ? 'true' : 'false');
    if (!ok) autoPruneState.danglingOnly = previous;
  }

  function onAutoPruneCronInput(e: Event) {
    autoPruneState.cron = (e.target as HTMLInputElement).value;
  }

  async function commitAutoPruneCron() {
    if (!autoPruneCronValidation.valid) return;
    await persistSetting(AUTO_PRUNE_KEYS.cron, autoPruneState.cron.trim());
  }

  async function persistNotificationSetting(key: string, value: string) {
    savingKey = key;
    const result = await saveNotificationSetting(key, value);
    savingKey = null;
    if (result.ok) {
      toast.success($t('settings.notifications.saved'));
    } else {
      toast.error(result.error || $t('settings.notifications.saveFailed'));
    }
    return result.ok;
  }

  async function toggleDiscordEnabled(e: Event) {
    const checked = (e.target as HTMLInputElement).checked;
    const previous = notificationsState.discordEnabled;
    notificationsState.discordEnabled = checked;
    const ok = await persistNotificationSetting(
      NOTIF_KEYS.discordEnabled,
      checked ? 'true' : 'false'
    );
    if (!ok) notificationsState.discordEnabled = previous;
  }

  function onWebhookInput(e: Event) {
    notificationsState.discordWebhookUrl = (e.target as HTMLInputElement).value;
    webhookDirty = true;
  }

  async function commitWebhook() {
    if (!webhookDirty) return;
    const ok = await persistNotificationSetting(
      NOTIF_KEYS.discordWebhookUrl,
      notificationsState.discordWebhookUrl.trim()
    );
    if (ok) {
      webhookDirty = false;
      // Reload to display the freshly masked value from the server.
      void loadNotificationSettings();
    }
  }

  async function handleTestWebhook() {
    testingWebhook = true;
    // If the user typed a new (unsaved) URL, test that; otherwise test the saved one.
    const override = webhookDirty ? notificationsState.discordWebhookUrl.trim() : undefined;
    const result = await testDiscordWebhook(override);
    testingWebhook = false;
    if (result.ok) {
      toast.success($t('settings.notifications.testSuccess'));
    } else {
      toast.error(result.error || $t('settings.notifications.testFailed'));
    }
  }
</script>

<div class="space-y-4 sm:space-y-6">
  <!-- Header -->
  <div>
    <h1 class="text-2xl font-bold text-gray-900 dark:text-white sm:text-3xl">{$t('settings.title')}</h1>
    <p class="text-sm text-gray-600 dark:text-gray-400 mt-1 sm:text-base">{$t('settings.subtitle')}</p>
  </div>

  {#if isAdmin.current}
    <!-- Tabs Navigation -->
    <Tabs bind:value={activeTab}>
      <TabsList class="flex w-full justify-start overflow-x-auto sm:inline-flex sm:w-auto sm:justify-center">
        <TabsTrigger value="general" active={activeTab === 'general'} onclick={() => activeTab = 'general'}>
          {$t('settings.tabs.general')}
        </TabsTrigger>
        <TabsTrigger value="update" active={activeTab === 'update'} onclick={() => activeTab = 'update'}>
          {$t('settings.tabs.appUpdate')}
        </TabsTrigger>
        <TabsTrigger value="projectUpdate" active={activeTab === 'projectUpdate'} onclick={() => activeTab = 'projectUpdate'}>
          {$t('settings.tabs.projectUpdate')}
        </TabsTrigger>
        <TabsTrigger value="notifications" active={activeTab === 'notifications'} onclick={() => activeTab = 'notifications'}>
          {$t('settings.tabs.notifications')}
        </TabsTrigger>
        <TabsTrigger value="registry" active={activeTab === 'registry'} onclick={() => activeTab = 'registry'}>
          {$t('settings.tabs.registry')}
        </TabsTrigger>
      </TabsList>

      <!-- General Tab -->
      <TabsContent value="general" active={activeTab === 'general'}>
        <Card>
          <CardHeader>
            <CardTitle>{$t('settings.general.title')}</CardTitle>
          </CardHeader>
          <CardContent>
            <p class="text-sm text-gray-600 dark:text-gray-400 mb-4">
              {$t('settings.general.logLevelDescription')}
            </p>

            <div class="flex flex-col gap-2 sm:flex-row sm:items-center sm:gap-4">
              <label for="log-level" class="text-sm font-medium text-gray-700 dark:text-gray-300">
                {$t('settings.general.logLevel')}
              </label>
              <select
                id="log-level"
                value={logLevelQuery.data?.current}
                onchange={handleLogLevelChange}
                disabled={logLevelQuery.isLoading || updateLogLevelMutation.isPending}
                class="h-10 w-full px-3 border border-gray-300 dark:border-gray-600 rounded-lg bg-white dark:bg-gray-700 text-gray-900 dark:text-white focus:ring-2 focus:ring-blue-500 focus:border-transparent disabled:opacity-50 cursor-pointer sm:h-auto sm:w-auto sm:py-2"
              >
                {#each logLevelQuery.data?.available ?? [] as level (level)}
                  <option value={level}>{$t(`settings.general.levels.${level.toLowerCase()}`)}</option>
                {/each}
              </select>
              {#if updateLogLevelMutation.isPending}
                <RefreshCw class="w-4 h-4 animate-spin text-gray-500" />
              {/if}
            </div>
          </CardContent>
        </Card>

        <Card class="mt-4 sm:mt-6">
          <CardHeader>
            <CardTitle>{$t('settings.composeEnv.title')}</CardTitle>
          </CardHeader>
          <CardContent>
            <p class="text-sm text-gray-600 dark:text-gray-400 mb-4">
              {$t('settings.composeEnv.description')}
            </p>

            <div class="flex flex-col gap-2 sm:flex-row sm:items-center">
              <label for="global-env-file" class="text-sm font-medium text-gray-700 dark:text-gray-300 sm:w-48">
                {$t('settings.composeEnv.pathLabel')}
              </label>
              <input
                id="global-env-file"
                type="text"
                bind:value={globalEnvFile}
                onblur={handleEnvFileBlur}
                placeholder={$t('settings.composeEnv.pathPlaceholder')}
                disabled={savingGlobalEnvFile}
                class="flex-1 px-3 py-2 border border-gray-300 dark:border-gray-600 rounded-lg bg-white dark:bg-gray-700 text-gray-900 dark:text-white focus:ring-2 focus:ring-blue-500 focus:border-transparent disabled:opacity-50"
              />
              <Button onclick={() => (showEnvFilePicker = true)} disabled={savingGlobalEnvFile}>
                {#if savingGlobalEnvFile}
                  <RefreshCw class="w-4 h-4 animate-spin mr-2" />
                {:else}
                  <FolderOpen class="w-4 h-4 mr-2" />
                {/if}
                {$t('settings.composeEnv.browse')}
              </Button>
            </div>
            <p class="text-xs text-gray-500 dark:text-gray-400 mt-2">
              {$t('settings.composeEnv.help')}
            </p>
          </CardContent>
        </Card>
      </TabsContent>

      <!-- App Update Tab -->
      <TabsContent value="update" active={activeTab === 'update'}>
        <Card>
          <CardHeader>
            <div class="flex flex-wrap items-center justify-between gap-2">
              <CardTitle>{$t('update.title')}</CardTitle>
              <Button
                size="sm"
                variant="outline"
                onclick={handleCheckUpdate}
                disabled={updateState.isCheckingUpdate}
                class="cursor-pointer"
              >
                {#if updateState.isCheckingUpdate}
                  <RefreshCw class="w-4 h-4 mr-2 animate-spin" />
                  {$t('update.checkingForUpdates')}
                {:else}
                  <RefreshCw class="w-4 h-4 mr-2" />
                  {$t('update.checkForUpdates')}
                {/if}
              </Button>
            </div>
          </CardHeader>
          <CardContent>
            <div class="space-y-4 sm:space-y-6">
              <!-- Dev Version Notice -->
              {#if updateState.updateInfo?.isDevVersion}
                <div class="p-3 bg-amber-50 dark:bg-amber-900/20 border border-amber-200 dark:border-amber-700 rounded-lg">
                  <div class="flex items-center gap-2 text-amber-700 dark:text-amber-400">
                    <AlertTriangle class="w-4 h-4 shrink-0" />
                    <span class="text-sm font-medium sm:text-base">{$t('update.devVersionNotice')}</span>
                  </div>
                  <p class="text-sm text-amber-600 dark:text-amber-500 mt-1">
                    {$t('update.devUpdateInfo')}
                  </p>
                </div>
              {/if}

              <!-- Version Info -->
              <div class="grid grid-cols-1 md:grid-cols-2 gap-3 sm:gap-4">
                <div class="p-3 bg-gray-50 dark:bg-gray-900 rounded-lg sm:p-4">
                  <p class="text-xs text-gray-500 dark:text-gray-400 mb-1 sm:text-sm">{$t('update.currentVersion')}</p>
                  <div class="flex items-center gap-2">
                    <p class="text-base font-semibold text-gray-900 dark:text-white sm:text-lg">
                      {updateState.updateInfo?.currentVersion ?? '-'}
                    </p>
                    {#if currentVersionDate}
                      <span class="text-xs text-gray-400 dark:text-gray-500">({currentVersionDate})</span>
                    {/if}
                  </div>
                </div>
                <div class="p-3 bg-gray-50 dark:bg-gray-900 rounded-lg sm:p-4">
                  <p class="text-xs text-gray-500 dark:text-gray-400 mb-1 sm:text-sm">{$t('update.latestVersion')}</p>
                  <div class="flex items-center gap-2 flex-wrap">
                    <p class="text-base font-semibold text-gray-900 dark:text-white sm:text-lg">
                      {updateState.updateInfo?.latestVersion ?? '-'}
                    </p>
                    {#if latestVersionDate}
                      <span class="text-xs text-gray-400 dark:text-gray-500">({latestVersionDate})</span>
                    {/if}
                    {#if updateState.updateInfo?.updateAvailable}
                      <Badge variant="success">{$t('update.updateAvailable')}</Badge>
                    {:else if updateState.updateInfo && !updateState.updateInfo.updateAvailable}
                      <Badge variant="secondary">
                        <CheckCircle class="w-3 h-3 mr-1" />
                        {$t('update.upToDate')}
                      </Badge>
                    {/if}
                  </div>
                </div>
              </div>

              <!-- Last Checked -->
              <div class="text-xs text-gray-500 dark:text-gray-400 sm:text-sm">
                {$t('update.lastChecked')}: {formatLastChecked(updateState.lastChecked)}
              </div>

              <!-- Update Available Section -->
              {#if updateState.updateInfo?.updateAvailable}
                <div class="border-t border-gray-200 dark:border-gray-700 pt-4 sm:pt-6">
                  {#if updateState.updateInfo.isDevVersion}
                    <!-- Dev version update: show digest-based info -->
                    <div class="mb-4">
                      <p class="text-sm text-gray-700 dark:text-gray-300 sm:text-base">
                        {$t('update.newerImageAvailable')}
                      </p>
                    </div>
                  {:else}
                    <!-- Release version: show changelog -->
                    <div class="flex flex-wrap items-center justify-between gap-2 mb-4">
                      <h3 class="text-base font-semibold sm:text-lg text-gray-900 dark:text-white">
                        {$t('update.changelog')}
                      </h3>
                      {#if updateState.updateInfo.releaseUrl}
                        <a
                          href={updateState.updateInfo.releaseUrl}
                          target="_blank"
                          rel="noopener noreferrer"
                          class="inline-flex items-center gap-1 text-sm text-primary hover:underline"
                        >
                          {$t('update.viewOnGitHub')}
                          <ExternalLink class="w-3 h-3" />
                        </a>
                      {/if}
                    </div>

                    <ChangelogDisplay
                      changelog={updateState.updateInfo.changelog}
                      summary={updateState.updateInfo.summary}
                    />
                  {/if}

                  <!-- Update Button -->
                  <div class="mt-4 pt-4 border-t border-gray-200 dark:border-gray-700 sm:mt-6 sm:pt-6">
                    <Button
                      onclick={handleUpdateNow}
                      disabled={triggerUpdateMutation.isPending}
                      class="w-full sm:w-auto cursor-pointer"
                    >
                      {#if triggerUpdateMutation.isPending}
                        <RefreshCw class="w-4 h-4 mr-2 animate-spin" />
                        {$t('update.updating')}
                      {:else}
                        <Download class="w-4 h-4 mr-2" />
                        {$t('update.updateNow')}
                      {/if}
                    </Button>
                  </div>
                </div>
              {:else if !updateState.updateInfo}
                <div class="text-center py-6 text-sm text-gray-500 dark:text-gray-400 sm:py-8 sm:text-base">
                  <RefreshCw class="w-10 h-10 mx-auto mb-3 opacity-50 sm:w-12 sm:h-12 sm:mb-4" />
                  <p>{$t('update.subtitle')}</p>
                  <p class="text-sm mt-2">Click "{$t('update.checkForUpdates')}" to get started</p>
                </div>
              {/if}
            </div>
          </CardContent>
        </Card>

        <!-- App Auto Update Card -->
        <Card class="mt-4 sm:mt-6">
          <CardHeader>
            <CardTitle>{$t('settings.autoUpdate.appTitle')}</CardTitle>
          </CardHeader>
          <CardContent>
            <p class="text-sm text-gray-600 dark:text-gray-400 mb-4">
              {$t('settings.autoUpdate.appDescription')}
            </p>

            <div class="space-y-4">
              <label class="flex min-h-10 items-center gap-3 cursor-pointer sm:min-h-0">
                <input
                  type="checkbox"
                  checked={autoUpdateState.appEnabled}
                  onchange={toggleAppEnabled}
                  disabled={savingKey === AUTO_UPDATE_KEYS.appEnabled}
                  class="w-4 h-4 rounded border-gray-300 text-primary focus:ring-primary cursor-pointer"
                />
                <span class="text-sm font-medium text-gray-700 dark:text-gray-300">
                  {$t('settings.autoUpdate.appEnableLabel')}
                </span>
                {#if savingKey === AUTO_UPDATE_KEYS.appEnabled}
                  <RefreshCw class="w-4 h-4 animate-spin text-gray-500" />
                {/if}
              </label>

              <div class="flex flex-col gap-1">
                <label for="app-cron" class="text-sm font-medium text-gray-700 dark:text-gray-300">
                  {$t('settings.autoUpdate.appCronLabel')}
                </label>
                <div class="flex items-center gap-2">
                  <input
                    id="app-cron"
                    type="text"
                    value={autoUpdateState.appCron}
                    oninput={onAppCronInput}
                    onblur={commitAppCron}
                    placeholder={$t('settings.autoUpdate.cronPlaceholder')}
                    disabled={!autoUpdateState.appEnabled || savingKey === AUTO_UPDATE_KEYS.appCron}
                    class="min-w-0 flex-1 px-3 py-2 border border-gray-300 dark:border-gray-600 rounded-lg bg-white dark:bg-gray-700 text-gray-900 dark:text-white font-mono focus:ring-2 focus:ring-blue-500 focus:border-transparent disabled:opacity-50"
                  />
                  {#if savingKey === AUTO_UPDATE_KEYS.appCron}
                    <RefreshCw class="w-4 h-4 animate-spin text-gray-500" />
                  {:else if appCronValidation.valid}
                    <Check class="w-4 h-4 text-green-600 dark:text-green-400" />
                  {:else}
                    <X class="w-4 h-4 text-red-600 dark:text-red-400" />
                  {/if}
                </div>
                {#if appCronValidation.valid}
                  <p class="text-xs text-gray-500 dark:text-gray-400 mt-1">
                    {appCronValidation.humanReadable ?? ''}
                    {#if appCronValidation.nextRun}
                      — {$t('settings.autoUpdate.nextRun')}: {formatNextRun(appCronValidation.nextRun)}
                      {#if appCountdown}
                        <span class="text-gray-400 dark:text-gray-500">({$t('settings.autoUpdate.countdownIn')} {appCountdown})</span>
                      {/if}
                    {/if}
                  </p>
                  <p class="text-xs text-gray-400 dark:text-gray-500 mt-0.5">
                    {$t('settings.autoUpdate.cronUtcHint')}
                  </p>
                {:else}
                  <p class="text-xs text-red-600 dark:text-red-400 mt-1">
                    {$t('settings.autoUpdate.cronInvalid')}
                  </p>
                {/if}
              </div>
            </div>
          </CardContent>
        </Card>
      </TabsContent>

      <!-- Project Update Check Tab -->
      <TabsContent value="projectUpdate" active={activeTab === 'projectUpdate'}>
        <Card>
          <CardHeader>
            <CardTitle>{$t('settings.projectUpdateCheck')}</CardTitle>
          </CardHeader>
          <CardContent>
            <p class="text-sm text-gray-600 dark:text-gray-400 mb-4">
              {$t('settings.projectUpdateCheckDescription')}
            </p>

            <div class="flex flex-col gap-2 sm:flex-row sm:items-center sm:gap-4">
              <label for="check-interval" class="text-sm font-medium text-gray-700 dark:text-gray-300">
                {$t('settings.checkInterval')}
              </label>
              <select
                id="check-interval"
                value={projectUpdateState.checkIntervalMinutes}
                onchange={handleIntervalChange}
                disabled={isSavingInterval}
                class="h-10 w-full px-3 border border-gray-300 dark:border-gray-600 rounded-lg bg-white dark:bg-gray-700 text-gray-900 dark:text-white focus:ring-2 focus:ring-blue-500 focus:border-transparent disabled:opacity-50 cursor-pointer sm:h-auto sm:w-auto sm:py-2"
              >
                {#each intervalOptions as option (option.value)}
                  <option value={option.value}>{option.label}</option>
                {/each}
              </select>
              {#if isSavingInterval}
                <RefreshCw class="w-4 h-4 animate-spin text-gray-500" />
              {/if}
            </div>
          </CardContent>
        </Card>

        <!-- Compose Auto Update Card -->
        <Card class="mt-4 sm:mt-6">
          <CardHeader>
            <CardTitle>{$t('settings.autoUpdate.composeTitle')}</CardTitle>
          </CardHeader>
          <CardContent>
            <p class="text-sm text-gray-600 dark:text-gray-400 mb-4">
              {$t('settings.autoUpdate.composeDescription')}
            </p>

            <div class="space-y-4">
              <label class="flex min-h-10 items-center gap-3 cursor-pointer sm:min-h-0">
                <input
                  type="checkbox"
                  checked={autoUpdateState.composeEnabled}
                  onchange={toggleComposeEnabled}
                  disabled={savingKey === AUTO_UPDATE_KEYS.composeEnabled}
                  class="w-4 h-4 rounded border-gray-300 text-primary focus:ring-primary cursor-pointer"
                />
                <span class="text-sm font-medium text-gray-700 dark:text-gray-300">
                  {$t('settings.autoUpdate.composeEnableLabel')}
                </span>
                {#if savingKey === AUTO_UPDATE_KEYS.composeEnabled}
                  <RefreshCw class="w-4 h-4 animate-spin text-gray-500" />
                {/if}
              </label>

              <div class="flex flex-col gap-1">
                <label for="compose-cron" class="text-sm font-medium text-gray-700 dark:text-gray-300">
                  {$t('settings.autoUpdate.composeCronLabel')}
                </label>
                <div class="flex items-center gap-2">
                  <input
                    id="compose-cron"
                    type="text"
                    value={autoUpdateState.composeCron}
                    oninput={onComposeCronInput}
                    onblur={commitComposeCron}
                    placeholder={$t('settings.autoUpdate.cronPlaceholder')}
                    disabled={!autoUpdateState.composeEnabled || savingKey === AUTO_UPDATE_KEYS.composeCron}
                    class="min-w-0 flex-1 px-3 py-2 border border-gray-300 dark:border-gray-600 rounded-lg bg-white dark:bg-gray-700 text-gray-900 dark:text-white font-mono focus:ring-2 focus:ring-blue-500 focus:border-transparent disabled:opacity-50"
                  />
                  {#if savingKey === AUTO_UPDATE_KEYS.composeCron}
                    <RefreshCw class="w-4 h-4 animate-spin text-gray-500" />
                  {:else if composeCronValidation.valid}
                    <Check class="w-4 h-4 text-green-600 dark:text-green-400" />
                  {:else}
                    <X class="w-4 h-4 text-red-600 dark:text-red-400" />
                  {/if}
                </div>
                {#if composeCronValidation.valid}
                  <p class="text-xs text-gray-500 dark:text-gray-400 mt-1">
                    {composeCronValidation.humanReadable ?? ''}
                    {#if composeCronValidation.nextRun}
                      — {$t('settings.autoUpdate.nextRun')}: {formatNextRun(composeCronValidation.nextRun)}
                      {#if composeCountdown}
                        <span class="text-gray-400 dark:text-gray-500">({$t('settings.autoUpdate.countdownIn')} {composeCountdown})</span>
                      {/if}
                    {/if}
                  </p>
                  <p class="text-xs text-gray-400 dark:text-gray-500 mt-0.5">
                    {$t('settings.autoUpdate.cronUtcHint')}
                  </p>
                {:else}
                  <p class="text-xs text-red-600 dark:text-red-400 mt-1">
                    {$t('settings.autoUpdate.cronInvalid')}
                  </p>
                {/if}
              </div>
            </div>
          </CardContent>
        </Card>

        <!-- Auto Prune Images Card -->
        <Card class="mt-4 sm:mt-6">
          <CardHeader>
            <CardTitle>{$t('settings.autoPrune.title')}</CardTitle>
          </CardHeader>
          <CardContent>
            <p class="text-sm text-gray-600 dark:text-gray-400 mb-4">
              {$t('settings.autoPrune.description')}
            </p>

            <div class="space-y-4">
              <label class="flex min-h-10 items-center gap-3 cursor-pointer sm:min-h-0">
                <input
                  type="checkbox"
                  checked={autoPruneState.enabled}
                  onchange={toggleAutoPruneEnabled}
                  disabled={savingKey === AUTO_PRUNE_KEYS.enabled}
                  class="w-4 h-4 rounded border-gray-300 text-primary focus:ring-primary cursor-pointer"
                />
                <span class="text-sm font-medium text-gray-700 dark:text-gray-300">
                  {$t('settings.autoPrune.enableLabel')}
                </span>
                {#if savingKey === AUTO_PRUNE_KEYS.enabled}
                  <RefreshCw class="w-4 h-4 animate-spin text-gray-500" />
                {/if}
              </label>

              <label class="flex min-h-10 items-center gap-3 cursor-pointer sm:min-h-0">
                <input
                  type="checkbox"
                  checked={autoPruneState.danglingOnly}
                  onchange={toggleAutoPruneDanglingOnly}
                  disabled={!autoPruneState.enabled || savingKey === AUTO_PRUNE_KEYS.danglingOnly}
                  class="w-4 h-4 rounded border-gray-300 text-primary focus:ring-primary cursor-pointer disabled:opacity-50"
                />
                <span class="flex flex-col">
                  <span class="text-sm font-medium text-gray-700 dark:text-gray-300">
                    {$t('settings.autoPrune.danglingOnlyLabel')}
                  </span>
                  <span class="text-xs text-gray-500 dark:text-gray-400">
                    {$t('settings.autoPrune.danglingOnlyHint')}
                  </span>
                </span>
                {#if savingKey === AUTO_PRUNE_KEYS.danglingOnly}
                  <RefreshCw class="w-4 h-4 animate-spin text-gray-500" />
                {/if}
              </label>

              <div class="flex flex-col gap-1">
                <label for="auto-prune-cron" class="text-sm font-medium text-gray-700 dark:text-gray-300">
                  {$t('settings.autoPrune.cronLabel')}
                </label>
                <div class="flex items-center gap-2">
                  <input
                    id="auto-prune-cron"
                    type="text"
                    value={autoPruneState.cron}
                    oninput={onAutoPruneCronInput}
                    onblur={commitAutoPruneCron}
                    placeholder={$t('settings.autoUpdate.cronPlaceholder')}
                    disabled={!autoPruneState.enabled || savingKey === AUTO_PRUNE_KEYS.cron}
                    class="min-w-0 flex-1 px-3 py-2 border border-gray-300 dark:border-gray-600 rounded-lg bg-white dark:bg-gray-700 text-gray-900 dark:text-white font-mono focus:ring-2 focus:ring-blue-500 focus:border-transparent disabled:opacity-50"
                  />
                  {#if savingKey === AUTO_PRUNE_KEYS.cron}
                    <RefreshCw class="w-4 h-4 animate-spin text-gray-500" />
                  {:else if autoPruneCronValidation.valid}
                    <Check class="w-4 h-4 text-green-600 dark:text-green-400" />
                  {:else}
                    <X class="w-4 h-4 text-red-600 dark:text-red-400" />
                  {/if}
                </div>
                {#if autoPruneCronValidation.valid}
                  <p class="text-xs text-gray-500 dark:text-gray-400 mt-1">
                    {autoPruneCronValidation.humanReadable ?? ''}
                    {#if autoPruneCronValidation.nextRun}
                      — {$t('settings.autoUpdate.nextRun')}: {formatNextRun(autoPruneCronValidation.nextRun)}
                      {#if autoPruneCountdown}
                        <span class="text-gray-400 dark:text-gray-500">({$t('settings.autoUpdate.countdownIn')} {autoPruneCountdown})</span>
                      {/if}
                    {/if}
                  </p>
                  <p class="text-xs text-gray-400 dark:text-gray-500 mt-0.5">
                    {$t('settings.autoUpdate.cronUtcHint')}
                  </p>
                {:else}
                  <p class="text-xs text-red-600 dark:text-red-400 mt-1">
                    {$t('settings.autoUpdate.cronInvalid')}
                  </p>
                {/if}
              </div>
            </div>
          </CardContent>
        </Card>
      </TabsContent>

      <!-- Notifications Tab -->
      <TabsContent value="notifications" active={activeTab === 'notifications'}>
        <Card>
          <CardHeader>
            <div class="flex items-center gap-2">
              <Bell class="w-5 h-5 text-gray-500" />
              <CardTitle>{$t('settings.notifications.discordTitle')}</CardTitle>
            </div>
          </CardHeader>
          <CardContent>
            <p class="text-sm text-gray-600 dark:text-gray-400 mb-4">
              {$t('settings.notifications.discordDescription')}
            </p>

            <div class="space-y-4">
              <label class="flex min-h-10 items-center gap-3 cursor-pointer sm:min-h-0">
                <input
                  type="checkbox"
                  checked={notificationsState.discordEnabled}
                  onchange={toggleDiscordEnabled}
                  disabled={savingKey === NOTIF_KEYS.discordEnabled}
                  class="w-4 h-4 rounded border-gray-300 text-primary focus:ring-primary cursor-pointer"
                />
                <span class="text-sm font-medium text-gray-700 dark:text-gray-300">
                  {$t('settings.notifications.enableLabel')}
                </span>
                {#if savingKey === NOTIF_KEYS.discordEnabled}
                  <RefreshCw class="w-4 h-4 animate-spin text-gray-500" />
                {/if}
              </label>

              <div class="flex flex-col gap-1">
                <label for="discord-webhook" class="text-sm font-medium text-gray-700 dark:text-gray-300">
                  {$t('settings.notifications.webhookUrl')}
                </label>
                <div class="flex items-center gap-2">
                  <input
                    id="discord-webhook"
                    type="text"
                    value={notificationsState.discordWebhookUrl}
                    oninput={onWebhookInput}
                    onblur={commitWebhook}
                    placeholder={$t('settings.notifications.webhookPlaceholder')}
                    disabled={savingKey === NOTIF_KEYS.discordWebhookUrl}
                    class="min-w-0 flex-1 px-3 py-2 border border-gray-300 dark:border-gray-600 rounded-lg bg-white dark:bg-gray-700 text-gray-900 dark:text-white font-mono text-sm focus:ring-2 focus:ring-blue-500 focus:border-transparent disabled:opacity-50"
                  />
                  {#if savingKey === NOTIF_KEYS.discordWebhookUrl}
                    <RefreshCw class="w-4 h-4 animate-spin text-gray-500" />
                  {/if}
                </div>
                <p class="text-xs text-gray-500 dark:text-gray-400 mt-1">
                  {$t('settings.notifications.webhookHelp')}
                </p>
              </div>

              <div>
                <Button
                  size="sm"
                  variant="outline"
                  onclick={handleTestWebhook}
                  disabled={testingWebhook || (!notificationsState.discordWebhookUrl && !webhookDirty)}
                  class="cursor-pointer"
                >
                  {#if testingWebhook}
                    <RefreshCw class="w-4 h-4 mr-2 animate-spin" />
                    {$t('settings.notifications.testing')}
                  {:else}
                    <Send class="w-4 h-4 mr-2" />
                    {$t('settings.notifications.testButton')}
                  {/if}
                </Button>
              </div>
            </div>
          </CardContent>
        </Card>
      </TabsContent>

      <!-- Registry Management Tab -->
      <TabsContent value="registry" active={activeTab === 'registry'}>
        <RegistryManagement />
      </TabsContent>
    </Tabs>
  {:else}
    <!-- Non-admin users see a message -->
    <Card>
      <CardContent>
        <div class="text-center py-6 text-sm text-gray-500 dark:text-gray-400 sm:py-8 sm:text-base">
          <Settings class="w-12 h-12 mx-auto mb-4 opacity-50" />
          <p>{$t('errors.unauthorized')}</p>
        </div>
      </CardContent>
    </Card>
  {/if}
</div>

<!-- Update Confirmation Dialog -->
<ConfirmDialog
  open={updateConfirmDialog.open}
  title={$t('update.confirmUpdate')}
  description={$t('update.confirmUpdateMessage')}
  confirmText={$t('update.updateNow')}
  confirmVariant="default"
  onconfirm={confirmUpdate}
  oncancel={() => updateConfirmDialog.open = false}
/>

{#if showEnvFilePicker}
  <FilePicker
    initialPath={envPickerInitialPath}
    onSelect={handleEnvFileSelected}
    onCancel={() => (showEnvFilePicker = false)}
  />
{/if}
