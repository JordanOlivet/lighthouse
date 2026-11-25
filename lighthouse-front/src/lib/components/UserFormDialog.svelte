<script lang="ts">
	import { createMutation, useQueryClient } from '@tanstack/svelte-query';
	import { usersApi, permissionsApi, oidcApi } from '$lib/api';
	import { onMount } from 'svelte';
	import { Button, Input, Label, Select } from '$lib/components/ui';
	import { PasswordInput, PermissionSelector } from '$lib/components';
	import { t } from '$lib/i18n';
	import { toast } from 'svelte-sonner';
	import type { User } from '$lib/types';
	import type { ResourcePermissionInput } from '$lib/types/permissions';
	import { validatePassword, type PasswordValidationError } from '$lib/utils/passwordValidation';

	interface Props {
		open: boolean;
		user?: User;
		onClose: () => void;
		onCopyPermissionsClick?: () => void;
	}

	let { open, user = undefined, onClose, onCopyPermissionsClick }: Props = $props();
 	let error = $state('');

	const isEditMode = $derived(!!user);

	// When SSO is enabled, an account can be created without password (SSO login only).
	let ssoEnabled = $state(false);
	let ssoName = $state('SSO');
	onMount(async () => {
		const config = await oidcApi.getOidcConfig();
		ssoEnabled = config.enabled;
		ssoName = config.displayName;
	});
	const queryClient = useQueryClient();

	let formData = $state({
		username: user?.username || '',
		email: user?.email || '',
		password: '',
		role: user?.role || 'user',
		isEnabled: user?.isEnabled ?? true,
		mustChangePassword: user?.mustChangePassword ?? false,
		mustAddEmail: user?.mustAddEmail ?? false,
		permissions: [] as ResourcePermissionInput[]
	});

	// Admins are never linked by email: they need a local password to sign in and link from the
	// user menu (also when an SSO-only account is promoted).
	const isAdminRole = $derived(formData.role.toLowerCase() === 'admin');
	const passwordRequired = $derived(
		isEditMode ? isAdminRole && user?.hasPassword === false : !ssoEnabled || isAdminRole
	);

	let permissionsModified = $state(false);
	let loadingPermissions = $state(false);

	// Reset form and load permissions when user changes
	$effect(() => {
		if (user) {
			formData = {
				username: user.username,
				email: user.email || '',
				password: '',
				role: user.role,
				isEnabled: user.isEnabled,
				mustChangePassword: user.mustChangePassword,
				mustAddEmail: user.mustAddEmail,
				permissions: []
			};
			permissionsModified = false;

			// Fetch existing permissions for this user
			loadingPermissions = true;
			permissionsApi.getUserPermissions(user.id).then((response) => {
				formData.permissions = response.directPermissions.map((p) => ({
					resourceType: p.resourceType,
					resourceName: p.resourceName,
					permissions: p.permissions
				}));
				loadingPermissions = false;
			}).catch(() => {
				loadingPermissions = false;
			});
		} else {
			permissionsModified = false;
		}
	});

	const createUserMutation = createMutation(() => ({
		mutationFn: () =>
			usersApi.create({
				username: formData.username,
				password: formData.password || undefined,
				email: formData.email || undefined,
				role: formData.role,
				permissions: formData.permissions
			}),
		onSuccess: () => {
			queryClient.invalidateQueries({ queryKey: ['users'] });
			toast.success($t('users.userCreated'));
			onClose();
		},
		onError: (err: any) => {
			// Extract detailed validation errors if available
			const responseData = err.response?.data;

			if (responseData?.errors && typeof responseData.errors === 'object') {
				const errorMessages = Object.values(responseData.errors)
				.flat()
				.filter((msg): msg is string => typeof msg === 'string');
				error = errorMessages.length > 0 ? errorMessages.join('.\n') : (responseData.message || $t('auth.loginFailed'));
			} else {
				error = responseData?.message || $t('auth.loginFailed');
			}
		}
	}));

	const updateUserMutation = createMutation(() => ({
		mutationFn: () =>
			usersApi.update(user!.id, {
				username: formData.username,
				email: formData.email || undefined,
				newPassword: formData.password || undefined,
				role: formData.role,
				isEnabled: formData.isEnabled,
				mustAddEmail: formData.mustAddEmail,
				permissions: formData.permissions,
				mustChangePassword: formData.mustChangePassword,
				...(permissionsModified ? { permissions: formData.permissions } : {})
			}),
		onSuccess: () => {
			queryClient.invalidateQueries({ queryKey: ['users'] });
			toast.success($t('users.userUpdated'));
			onClose();
		},
		onError: (err: any) => {
			// Extract detailed validation errors if available
			const responseData = err.response?.data;

			if (responseData?.errors && typeof responseData.errors === 'object') {
				const errorMessages = Object.values(responseData.errors)
				.flat()
				.filter((msg): msg is string => typeof msg === 'string');
				error = errorMessages.length > 0 ? errorMessages.join('.\n') : (responseData.message || $t('auth.loginFailed'));
			} else {
				error = responseData?.message || $t('auth.loginFailed');
			}
		}
	}));

	let validationErrors = $state<PasswordValidationError[]>([]);

	function handleSubmit(e: Event) {
		e.preventDefault();
		validationErrors = [];

		if (!formData.username || (passwordRequired && !formData.password)) {
			error = 'Please fill in all required fields';
			return;
		}

		// A password-less account can only sign in via SSO, which links it by email.
		if (!isEditMode && !formData.password && !formData.email) {
			error = $t('users.oidc.emailRequiredWithoutPassword', { name: ssoName });
			return;
		}

		// Validate password if provided (required for create, optional for edit)
		if (formData.password) {
			const validation = validatePassword(formData.password);
			if (!validation.isValid) {
				validationErrors = validation.errors;
				return;
			}
		}

		if (isEditMode) {
			updateUserMutation.mutate();
		} else {
			createUserMutation.mutate();
		}
	}

	function handlePermissionsChange(permissions: ResourcePermissionInput[]) {
		formData.permissions = permissions;
		permissionsModified = true;
	}
</script>

{#if open}
	<div
		class="fixed inset-0 bg-black/50 dark:bg-black/70 backdrop-blur-sm flex items-center justify-center z-50 p-4"
	>
		<div
			class="bg-white dark:bg-gray-800 rounded-2xl shadow-2xl border border-gray-200 dark:border-gray-700 w-full max-w-3xl max-h-[90vh] overflow-auto"
		>
			<div class="p-6 border-b border-gray-200 dark:border-gray-700">
				<h2 class="text-2xl font-bold text-gray-900 dark:text-white">
					{isEditMode ? $t('users.editUser') : $t('users.createUser')}
				</h2>
			</div>

			<form onsubmit={handleSubmit} class="p-6 space-y-6">
				<!-- Username -->
				<div class="space-y-2">
					<Label for="username">{$t('users.username')} *</Label>
					<Input
						id="username"
						type="text"
						bind:value={formData.username}
						placeholder={$t('users.username')}
						required
					/>
				</div>

				<!-- Email -->
				<div class="space-y-2">
					<Label for="email">Email ({$t('common.optional')})</Label>
					<Input
						id="email"
						type="email"
						bind:value={formData.email}
						placeholder="user@example.com"
					/>
					<p class="text-xs text-gray-500 dark:text-gray-400">
						Required for password reset functionality
					</p>
				</div>

				<!-- Password -->
				<div class="space-y-2">
					<Label for="password"
						>{$t('users.password')} {passwordRequired ? '*' : `(${$t('common.optional')})`}</Label
					>
					<PasswordInput
						id="password"
						bind:value={formData.password}
						placeholder={isEditMode
							? $t('users.leaveBlankToKeepCurrent')
							: $t('users.password')}
						required={passwordRequired}
					/>
					{#if ssoEnabled && isAdminRole && passwordRequired}
						<p class="text-xs text-gray-500 dark:text-gray-400">
							{$t('users.oidc.adminPasswordRequired', { name: ssoName })}
						</p>
					{:else if ssoEnabled && !isEditMode}
						<p class="text-xs text-gray-500 dark:text-gray-400">
							{$t('users.oidc.passwordOptionalHint', { name: ssoName })}
						</p>
					{/if}
				</div>

				<!-- Role -->
				<div class="space-y-2">
					<Label for="role">{$t('users.role')} *</Label>
					<select
						id="role"
						bind:value={formData.role}
						class="w-full border border-gray-300 dark:border-gray-600 rounded-lg px-4 py-2.5 bg-white dark:bg-gray-700 text-gray-900 dark:text-white focus:ring-2 focus:ring-blue-500 focus:border-transparent transition-all"
					>
						<option value="user">User</option>
						<option value="admin">Admin</option>
					</select>
				</div>

				<!-- Status & Flags -->
				<div class="grid grid-cols-2 gap-4">
					<label class="flex items-center gap-2 cursor-pointer">
						<input type="checkbox" bind:checked={formData.isEnabled} class="h-4 w-4" />
						<span class="text-sm font-medium text-gray-900 dark:text-white"
							>{$t('users.enabled')}</span
						>
					</label>

					<label class="flex items-center gap-2 cursor-pointer">
						<input type="checkbox" bind:checked={formData.mustChangePassword} class="h-4 w-4" />
						<span class="text-sm font-medium text-gray-900 dark:text-white"
							>{$t('users.mustChangePassword')}</span
						>
					</label>

					<label class="flex items-center gap-2 cursor-pointer">
						<input type="checkbox" bind:checked={formData.mustAddEmail} class="h-4 w-4" />
						<span class="text-sm font-medium text-gray-900 dark:text-white">
							Must add email
						</span>
					</label>
				</div>

				<!-- Permissions -->
				<div class="border-t border-gray-200 dark:border-gray-700 pt-6">
					{#if loadingPermissions}
						<div class="flex items-center gap-2 text-sm text-gray-500 dark:text-gray-400 py-4">
							<svg class="animate-spin h-4 w-4" xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 24 24">
								<circle class="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" stroke-width="4"></circle>
								<path class="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z"></path>
							</svg>
							{$t('common.loading')}...
						</div>
					{:else}
						<PermissionSelector
							permissions={formData.permissions}
							onChange={handlePermissionsChange}
							onCopyClick={onCopyPermissionsClick}
							showCopyButton={isEditMode && !!onCopyPermissionsClick}
						/>
					{/if}
				</div>

				{#if validationErrors.length > 0}
					<div class="mb-4 p-3 bg-yellow-100 dark:bg-yellow-900/30 border border-yellow-400 dark:border-yellow-700 rounded-lg">
						<h3 class="text-sm font-semibold text-yellow-800 dark:text-yellow-300">{$t('auth.passwordRequirements')}:</h3>
						<ul class="mt-2 list-disc pl-5 text-sm text-yellow-700 dark:text-yellow-400 space-y-1">
							{#each validationErrors as err}
								<li>{$t(err.key, err.params)}</li>
							{/each}
						</ul>
					</div>
				{/if}

				{#if error}
					<div class="mb-4 p-3 bg-red-100 dark:bg-red-900/30 border border-red-400 dark:border-red-700 text-red-700 dark:text-red-300 rounded-lg text-sm whitespace-pre-line">
					{error}
					</div>
				{/if}

				<!-- Actions -->
				<div class="flex gap-3 pt-4 border-t border-gray-200 dark:border-gray-700">
					<Button
						type="submit"
						disabled={createUserMutation.isPending || updateUserMutation.isPending}
						class="flex-1"
					>
						{#if createUserMutation.isPending || updateUserMutation.isPending}
							{$t('common.saving')}...
						{:else}
							{isEditMode ? $t('common.update') : $t('common.create')}
						{/if}
					</Button>
					<Button
						type="button"
						variant="outline"
						onclick={onClose}
						disabled={createUserMutation.isPending || updateUserMutation.isPending}
						class="flex-1"
					>
						{$t('common.cancel')}
					</Button>
				</div>
			</form>
		</div>
	</div>
{/if}
