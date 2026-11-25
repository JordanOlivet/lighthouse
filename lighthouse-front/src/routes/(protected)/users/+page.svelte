<script lang="ts">
  import { createQuery, createMutation, useQueryClient } from '@tanstack/svelte-query';
  import { Users, Plus, Edit, Trash2, Check, X, Unlink } from 'lucide-svelte';
  import usersApi from '$lib/api/users';
  import type { User } from '$lib/types';
  import LoadingState from '$lib/components/common/LoadingState.svelte';
  import ConfirmDialog from '$lib/components/common/ConfirmDialog.svelte';
  import UserFormDialog from '$lib/components/UserFormDialog.svelte';
  import Button from '$lib/components/ui/button.svelte';
  import Badge from '$lib/components/ui/badge.svelte';
  import { t } from '$lib/i18n';
  import { toast } from 'svelte-sonner';

  let confirmDialog = $state<{ open: boolean; title: string; description: string; onConfirm: () => void }>({
    open: false,
    title: '',
    description: '',
    onConfirm: () => {},
  });

  let userFormDialog = $state<{ open: boolean; user?: User }>({
    open: false,
    user: undefined,
  });

  const queryClient = useQueryClient();

  const usersQuery = createQuery(() => ({
    queryKey: ['users'],
    queryFn: () => usersApi.list(),
  }));

  const enableMutation = createMutation(() => ({
    mutationFn: (id: number) => usersApi.enable(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['users'] });
      toast.success($t('users.userStatusUpdated'));
    },
    onError: () => toast.error($t('users.failedToUpdate')),
  }));

  const disableMutation = createMutation(() => ({
    mutationFn: (id: number) => usersApi.disable(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['users'] });
      toast.success($t('users.userStatusUpdated'));
    },
    onError: () => toast.error($t('users.failedToUpdate')),
  }));

  const deleteMutation = createMutation(() => ({
    mutationFn: (id: number) => usersApi.delete(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['users'] });
      toast.success($t('users.userDeleted'));
    },
    onError: () => toast.error($t('users.failedToDelete')),
  }));

  const unlinkOidcMutation = createMutation(() => ({
    mutationFn: (id: number) => usersApi.unlinkOidc(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['users'] });
      toast.success($t('users.oidc.unlinked'));
    },
    onError: () => toast.error($t('users.failedToUpdate')),
  }));

  function confirmUnlinkOidc(userId: number, username: string) {
    confirmDialog = {
      open: true,
      title: $t('users.oidc.unlink'),
      description: $t('users.oidc.unlinkConfirm', { username }),
      onConfirm: () => {
        unlinkOidcMutation.mutate(userId);
        confirmDialog.open = false;
      },
    };
  }

  function confirmDelete(userId: number, username: string) {
    confirmDialog = {
      open: true,
     title: $t('users.deleteUser'),
      description: `Are you sure you want to delete user "${username}"?`,
      onConfirm: () => {
        deleteMutation.mutate(userId);
        confirmDialog.open = false;
      },
    };
  }

  function formatDate(dateStr: string): string {
    return new Date(dateStr).toLocaleDateString(undefined, {
      year: 'numeric',
      month: 'short',
      day: 'numeric',
    });
  }
</script>

<div class="space-y-6">
  <!-- Header -->
  <div class="flex flex-col sm:flex-row justify-between items-start sm:items-center gap-4">
    <div>
      <h1 class="text-3xl font-bold text-gray-900 dark:text-white">{$t('users.title')}</h1>
      <p class="text-gray-600 dark:text-gray-400 mt-1">{$t('users.subtitle')}</p>
    </div>
    <Button onclick={() => userFormDialog = { open: true, user: undefined }}>
      <Plus class="w-4 h-4 mr-2" />
      {$t('users.createUser')}
    </Button>
  </div>

  <!-- Users List -->
  {#if usersQuery.isLoading}
    <LoadingState message={$t('common.loading')} />
  {:else if usersQuery.error}
    <div class="text-center py-8 text-red-500">
      {$t('errors.generic')}
    </div>
  {:else if !usersQuery.data || usersQuery.data.length === 0}
    <div class="text-center py-12 bg-white dark:bg-gray-800 rounded-lg shadow">
      <Users class="w-12 h-12 mx-auto text-gray-400 mb-4" />
      <p class="text-gray-600 dark:text-gray-400">{$t('users.noUsers')}</p>
    </div>
  {:else}
    <div class="bg-white dark:bg-gray-800 rounded-lg shadow overflow-hidden">
      <table class="min-w-full divide-y divide-gray-200 dark:divide-gray-700">
        <thead class="bg-gray-50 dark:bg-gray-900">
          <tr>
            <th class="px-6 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">
              {$t('users.username')}
            </th>
            <th class="px-6 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">
              {$t('users.role')}
            </th>
            <th class="px-6 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">
              {$t('users.status')}
            </th>
            <th class="px-6 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">
              {$t('users.createdAt')}
            </th>
            <th class="px-6 py-3 text-right text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">
              {$t('users.actions')}
            </th>
          </tr>
        </thead>
        <tbody class="divide-y divide-gray-200 dark:divide-gray-700">
          {#each usersQuery.data as user (user.id)}
            <tr class="hover:bg-gray-50 dark:hover:bg-gray-700/50">
              <td class="px-6 py-4 whitespace-nowrap">
                <div class="flex items-center gap-3">
                  <div class="w-8 h-8 rounded-full bg-blue-500 flex items-center justify-center text-white font-semibold text-sm">
                    {user.username.charAt(0).toUpperCase()}
                  </div>
                  <span class="font-medium text-gray-900 dark:text-white">{user.username}</span>
                  {#if user.oidcLinked}
                    <Badge variant="secondary">{$t('users.oidc.badge')}</Badge>
                  {/if}
                </div>
              </td>
              <td class="px-6 py-4 whitespace-nowrap">
                <Badge variant={user.role === 'Admin' ? 'default' : 'secondary'}>
                  {user.role}
                </Badge>
              </td>
              <td class="px-6 py-4 whitespace-nowrap">
                <Badge variant={user.isEnabled ? 'success' : 'destructive'}>
                  {user.isEnabled ? $t('users.enabled') : $t('users.disabled')}
                </Badge>
              </td>
              <td class="px-6 py-4 whitespace-nowrap text-sm text-gray-600 dark:text-gray-400">
                {formatDate(user.createdAt)}
              </td>
              <td class="px-6 py-4 whitespace-nowrap text-right">
                <div class="flex justify-end gap-2">
                  <button
                    onclick={() => userFormDialog = { open: true, user }}
                    class="p-2 text-blue-600 hover:bg-blue-100 dark:hover:bg-blue-900/30 rounded-lg transition-colors cursor-pointer"
                    title={$t('common.edit')}
                  >
                    <Edit class="w-4 h-4" />
                  </button>
                  {#if user.oidcLinked}
                    <button
                      onclick={() => confirmUnlinkOidc(user.id, user.username)}
                      class="p-2 text-gray-600 hover:bg-gray-100 dark:text-gray-300 dark:hover:bg-gray-700 rounded-lg transition-colors cursor-pointer"
                      title={$t('users.oidc.unlink')}
                      disabled={unlinkOidcMutation.isPending}
                    >
                      <Unlink class="w-4 h-4" />
                    </button>
                  {/if}
                  {#if user.isEnabled}
                    <button
                      onclick={() => disableMutation.mutate(user.id)}
                      class="p-2 text-yellow-600 hover:bg-yellow-100 dark:hover:bg-yellow-900/30 rounded-lg transition-colors cursor-pointer"
                      title="Disable"
                      disabled={disableMutation.isPending}
                    >
                      <X class="w-4 h-4" />
                    </button>
                  {:else}
                    <button
                      onclick={() => enableMutation.mutate(user.id)}
                      class="p-2 text-green-600 hover:bg-green-100 dark:hover:bg-green-900/30 rounded-lg transition-colors cursor-pointer"
                      title="Enable"
                      disabled={enableMutation.isPending}
                    >
                      <Check class="w-4 h-4" />
                    </button>
                  {/if}
                  <button
                    onclick={() => confirmDelete(user.id, user.username)}
                    class="p-2 text-red-600 hover:bg-red-100 dark:hover:bg-red-900/30 rounded-lg transition-colors cursor-pointer"
                    title={$t('common.delete')}
                    disabled={deleteMutation.isPending}
                  >
                    <Trash2 class="w-4 h-4" />
                  </button>
                </div>
              </td>
            </tr>
          {/each}
        </tbody>
      </table>
    </div>
  {/if}
</div>

<ConfirmDialog
  open={confirmDialog.open}
  title={confirmDialog.title}
  description={confirmDialog.description}
  onconfirm={confirmDialog.onConfirm}
  oncancel={() => confirmDialog.open = false}
/>

<UserFormDialog
  open={userFormDialog.open}
  user={userFormDialog.user}
  onClose={() => userFormDialog = { open: false, user: undefined }}
/>
