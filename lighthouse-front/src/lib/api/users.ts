import { apiClient } from './client';
import type { ResourcePermissionInput, User } from '$lib/types';

export interface CreateUserRequest {
  username: string;
  /** Empty = OIDC-only account (only accepted when SSO is enabled). */
  password?: string;
  email?: string;
  role: string;
  permissions?: ResourcePermissionInput[];
}

export interface UpdateUserRequest {
  username?: string;
  email?: string;
  role?: string;
  isEnabled?: boolean;
  mustAddEmail?: boolean;
  mustChangePassword?: boolean;
  newPassword?: string;
  permissions?: ResourcePermissionInput[];
}

const usersApi = {
  /**
   * Get all users (admin only)
   */
  list: async (): Promise<User[]> => {
    const response = await apiClient.get('/users', {
      params: { pageSize: 1000 } // Get all users without pagination
    });
    // Backend returns paginated response with Items property
    return response.data.data.items || response.data.data.Items || [];
  },

  /**
   * Get user by ID
   */
  get: async (id: number): Promise<User> => {
    const response = await apiClient.get(`/users/${id}`);
    return response.data.data;
  },

  /**
   * Create new user (admin only)
   */
  create: async (data: CreateUserRequest): Promise<User> => {
    // Transform to PascalCase for C# backend
    const backendData = {
      Username: data.username,
      Password: data.password || null,
      Email: data.email || null,
      Role: data.role,
      Permissions: data.permissions?.map(p => ({
        ResourceType: p.resourceType,
        ResourceName: p.resourceName,
        Permissions: p.permissions
      })) || null
    };
    const response = await apiClient.post('/users', backendData);
    return response.data.data;
  },

  /**
   * Remove the SSO (OIDC) identity link of a user (admin only)
   */
  unlinkOidc: async (id: number): Promise<User> => {
    const response = await apiClient.delete(`/users/${id}/oidc-link`);
    return response.data.data;
  },

  /**
   * Update user (admin only)
   */
  update: async (id: number, data: UpdateUserRequest): Promise<User> => {
    // Transform to PascalCase for C# backend
    const backendData: any = {};
    if (data.username !== undefined) backendData.Username = data.username;
    if (data.email !== undefined) backendData.Email = data.email;
    if (data.mustAddEmail !== undefined) backendData.MustAddEmail = data.mustAddEmail;
    if (data.role !== undefined) backendData.Role = data.role;
    if (data.isEnabled !== undefined) backendData.IsEnabled = data.isEnabled;
    if (data.mustChangePassword !== undefined) backendData.MustChangePassword = data.mustChangePassword;
    if (data.newPassword !== undefined) backendData.NewPassword = data.newPassword;
    if (data.permissions !== undefined) {
      backendData.Permissions = data.permissions?.map(p => ({
        ResourceType: p.resourceType,
        ResourceName: p.resourceName,
        Permissions: p.permissions
      })) || null;
    }

    const response = await apiClient.put(`/users/${id}`, backendData);
    return response.data.data;
  },

  /**
   * Delete user (admin only)
   */
  delete: async (id: number): Promise<void> => {
    await apiClient.delete(`/users/${id}`);
  },

  /**
   * Enable user account (admin only)
   */
  enable: async (id: number): Promise<User> => {
    const response = await apiClient.put(`/users/${id}/enable`);
    return response.data.data;
  },

  /**
   * Disable user account (admin only)
   */
  disable: async (id: number): Promise<User> => {
    const response = await apiClient.put(`/users/${id}/disable`);
    return response.data.data;
  },
};

export default usersApi;
