import { browser } from '$app/env';
import type { TableId, ColumnPreferences } from '$lib/types/table';

const STORAGE_KEY_PREFIX = 'column-prefs-';

/**
 * Creates a column preferences store for a specific table
 * @param tableId - Unique identifier for the table
 * @param defaultOrder - Default column order (all column IDs)
 */
export function createColumnPreferences(tableId: TableId, defaultOrder: string[]) {
  const storageKey = `${STORAGE_KEY_PREFIX}${tableId}`;

  function loadFromStorage(): string[] {
    if (!browser) return defaultOrder;

    try {
      const stored = localStorage.getItem(storageKey);
      if (!stored) return defaultOrder;

      const prefs: ColumnPreferences = JSON.parse(stored);

      // Validate and reconcile: handle added/removed columns
      const storedSet = new Set(prefs.order);
      const defaultSet = new Set(defaultOrder);

      // Filter out columns that no longer exist
      const validStored = prefs.order.filter(id => defaultSet.has(id));

      // Add new columns that weren't in storage (append at end)
      const newColumns = defaultOrder.filter(id => !storedSet.has(id));

      return [...validStored, ...newColumns];
    } catch {
      return defaultOrder;
    }
  }

  function saveToStorage(order: string[]) {
    if (!browser) return;

    const prefs: ColumnPreferences = { order };
    localStorage.setItem(storageKey, JSON.stringify(prefs));
  }

  // Initialize state
  let order = $state<string[]>(loadFromStorage());

  return {
    /** Current column order */
    get order() {
      return order;
    },

    /**
     * Move a column from one position to another
     * @param fromIndex - Source index
     * @param toIndex - Destination index
     */
    moveColumn(fromIndex: number, toIndex: number) {
      if (fromIndex === toIndex) return;
      if (fromIndex < 0 || fromIndex >= order.length) return;
      if (toIndex < 0 || toIndex >= order.length) return;

      const newOrder = [...order];
      const [removed] = newOrder.splice(fromIndex, 1);
      newOrder.splice(toIndex, 0, removed);

      order = newOrder;
      saveToStorage(order);
    },

    /**
     * Reset to default column order
     */
    reset() {
      order = [...defaultOrder];
      saveToStorage(order);
    }
  };
}

export type ColumnPreferencesStore = ReturnType<typeof createColumnPreferences>;
