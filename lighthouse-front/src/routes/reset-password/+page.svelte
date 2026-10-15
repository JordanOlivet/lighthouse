<script lang="ts">
	import { authApi } from '$lib/api/auth';
	import { goto } from '$app/navigation';
	import { onMount } from 'svelte';
	import { page } from '$app/state';
	import Button from '$lib/components/ui/button.svelte';
	import Card from '$lib/components/ui/card.svelte';
	import CardHeader from '$lib/components/ui/card-header.svelte';
	import CardTitle from '$lib/components/ui/card-title.svelte';
	import CardContent from '$lib/components/ui/card-content.svelte';
	import PasswordInput from '$lib/components/common/PasswordInput.svelte';
	import { Lock, AlertCircle, Loader2 } from 'lucide-svelte';
	import { t } from '$lib/i18n';
	import { validatePassword, type PasswordValidationError } from '$lib/utils/passwordValidation';

	let token = $state('');
	let newPassword = $state('');
	let confirmPassword = $state('');
	let isValidatingToken = $state(true);
	let isTokenValid = $state(false);
	let isSubmitting = $state(false);
	let errorMessage = $state('');
	let validationErrors = $state<PasswordValidationError[]>([]);

	onMount(async () => {
		const urlToken = page.url.searchParams.get('token');
		if (!urlToken) {
			errorMessage = 'Missing reset token. Please use the link from your email.';
			isValidatingToken = false;
			return;
		}

		token = urlToken;

		try {
			const valid = await authApi.validateResetToken(token);
			if (valid) {
				isTokenValid = true;
			} else {
				errorMessage = 'Invalid or expired reset token. Please request a new password reset.';
			}
		} catch {
			errorMessage = 'Invalid or expired reset token. Please request a new password reset.';
		} finally {
			isValidatingToken = false;
		}
	});

	function validateForm(): boolean {
		validationErrors = [];

		const validation = validatePassword(newPassword);
		if (!validation.isValid) {
			validationErrors = [...validation.errors];
		}

		if (newPassword !== confirmPassword) {
			validationErrors.push({ key: 'auth.passwordMismatch' });
		}

		return validationErrors.length === 0;
	}

	async function handleSubmit(e: SubmitEvent) {
		e.preventDefault();

		if (!validateForm()) {
			return;
		}

		isSubmitting = true;
		errorMessage = '';

		try {
			await authApi.resetPassword(token, newPassword);
			goto('/login?reset=success');
		} catch (error: any) {
			errorMessage = error.response?.data?.message || 'Failed to reset password. Please try again.';
		} finally {
			isSubmitting = false;
		}
	}
</script>

<div class="min-h-screen flex items-center justify-center bg-linear-to-br from-gray-100 to-gray-200 dark:from-gray-900 dark:to-gray-800">
	<Card class="max-w-md w-full mx-4 shadow-xl">
		<CardHeader class="text-center pb-2">
			<div class="mx-auto w-16 h-16 bg-linear-to-br from-blue-500 to-blue-600 rounded-xl flex items-center justify-center shadow-lg mb-4">
				<Lock class="w-8 h-8 text-white" />
			</div>
			<CardTitle class="text-2xl font-bold">Set New Password</CardTitle>
			<p class="text-sm text-gray-600 dark:text-gray-400 mt-2">
				Choose a strong password for your account
			</p>
		</CardHeader>

		<CardContent class="pt-6">
			{#if isValidatingToken}
				<div class="flex flex-col items-center justify-center py-8">
					<Loader2 class="w-12 h-12 text-blue-600 dark:text-blue-400 animate-spin mb-4" />
					<p class="text-sm text-gray-600 dark:text-gray-400">Validating reset token...</p>
				</div>
			{:else if !isTokenValid}
				<div class="mb-4 p-3 bg-red-100 dark:bg-red-900/30 border border-red-400 dark:border-red-700 text-red-700 dark:text-red-300 rounded-lg text-sm">
					{errorMessage}
				</div>

				<div class="space-y-3">
					<Button onclick={() => goto('/forgot-password')} class="w-full">
						Request New Password Reset
					</Button>
					<div class="text-center">
						<a
							href="/login"
							class="text-sm font-medium text-blue-600 hover:text-blue-500 dark:text-blue-400 dark:hover:text-blue-300"
						>
							← Back to login
						</a>
					</div>
				</div>
			{:else}
				{#if errorMessage}
					<div class="mb-4 p-3 bg-red-100 dark:bg-red-900/30 border border-red-400 dark:border-red-700 text-red-700 dark:text-red-300 rounded-lg text-sm">
						{errorMessage}
					</div>
				{/if}

				{#if validationErrors.length > 0}
					<div class="mb-4 p-3 bg-yellow-100 dark:bg-yellow-900/30 border border-yellow-400 dark:border-yellow-700 rounded-lg">
						<div class="flex items-start gap-2">
							<AlertCircle class="w-5 h-5 text-yellow-600 dark:text-yellow-400 flex-shrink-0 mt-0.5" />
							<div>
								<h3 class="text-sm font-semibold text-yellow-800 dark:text-yellow-300">{$t('auth.passwordRequirements')}:</h3>
								<ul class="mt-2 list-disc pl-5 text-sm text-yellow-700 dark:text-yellow-400 space-y-1">
									{#each validationErrors as err}
										<li>{$t(err.key, err.params)}</li>
									{/each}
								</ul>
							</div>
						</div>
					</div>
				{/if}

				<form onsubmit={handleSubmit} class="space-y-4">
					<div>
						<label for="newPassword" class="block text-sm font-medium text-gray-700 dark:text-gray-200 mb-2">
							New Password
						</label>
						<PasswordInput
							id="newPassword"
							bind:value={newPassword}
							required
							autocomplete="new-password"
							placeholder="Enter new password (min 8 characters)"
						/>
					</div>

					<div>
						<label for="confirmPassword" class="block text-sm font-medium text-gray-700 dark:text-gray-200 mb-2">
							Confirm Password
						</label>
						<PasswordInput
							id="confirmPassword"
							bind:value={confirmPassword}
							required
							autocomplete="new-password"
							placeholder="Confirm new password"
						/>
					</div>

					<Button type="submit" disabled={isSubmitting} class="w-full">
						{#if isSubmitting}
							<span class="flex items-center justify-center gap-2">
								<span class="w-4 h-4 border-2 border-white border-t-transparent rounded-full animate-spin"></span>
								Resetting password...
							</span>
						{:else}
							Reset Password
						{/if}
					</Button>

					<div class="text-center">
						<a
							href="/login"
							class="text-sm font-medium text-blue-600 hover:text-blue-500 dark:text-blue-400 dark:hover:text-blue-300"
						>
							← Back to login
						</a>
					</div>
				</form>
			{/if}
		</CardContent>
	</Card>
</div>
