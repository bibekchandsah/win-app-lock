<script lang="ts">
  import type { ShelfRow } from '../motion';

  export let items: ShelfRow[];
  export let onAdd: () => void;
  export let onRemove: (path: string) => void;
  export let onClear: () => void;

  let confirmClear = false;
  let confirmTimer: ReturnType<typeof setTimeout> | undefined;

  function clear(): void {
    if (!confirmClear) {
      confirmClear = true;
      clearTimeout(confirmTimer);
      confirmTimer = setTimeout(() => (confirmClear = false), 3000);
      return;
    }

    confirmClear = false;
    clearTimeout(confirmTimer);
    onClear();
  }

  function remove(event: MouseEvent, path: string): void {
    event.stopPropagation();
    onRemove(path);
  }
</script>

<div class="shelf-tab">
  <div class="shelf-bar">
    <button type="button" class="pill-btn" onclick={onAdd} title="Add files to the shelf">
      <span class="glyph">{'\uE710'}</span>
      Add files
    </button>
    {#if items.length > 0}
      <button type="button" class="pill-btn" class:armed={confirmClear} onclick={clear}>
        {confirmClear ? 'Clear all?' : 'Clear all'}
      </button>
    {/if}
    <span class="shelf-count">{items.length > 0 ? `${items.length} item${items.length === 1 ? '' : 's'}` : ''}</span>
  </div>

  {#if items.length === 0}
    <p class="empty">
      Park files here while you work: they stay as links to the originals, and nothing is uploaded.
    </p>
  {:else}
    <div class="shelf-grid">
      {#each items as item (item.path)}
        <div class="shelf-item" class:missing={!item.exists} title={item.path}>
          <button
            type="button"
            class="shelf-remove"
            aria-label="Remove from the shelf"
            title="Remove from the shelf"
            onclick={(event) => remove(event, item.path)}
          >
            <span class="glyph">{'\uE711'}</span>
          </button>
          <span class="shelf-icon">
            {#if item.icon}
              <img src={`https://island.cache/shelf/${item.icon}`} alt="" draggable="false" />
            {:else}
              <span class="glyph">{'\uE7C3'}</span>
            {/if}
          </span>
          <span class="shelf-name">{item.name}</span>
        </div>
      {/each}
    </div>
  {/if}
</div>
