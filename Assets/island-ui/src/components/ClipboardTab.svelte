<script lang="ts">
  import type { ClipboardRow } from '../motion';

  export let items: ClipboardRow[];
  export let query: string;
  export let onSearch: (query: string) => void;
  export let onPaste: (id: string, copyOnly: boolean) => void;
  export let onPin: (id: string) => void;
  export let onClear: () => void;

  let confirmClear = false;
  let confirmTimer: ReturnType<typeof setTimeout> | undefined;

  function typeGlyph(type: string): string {
    if (type === 'image') return '\uE91B';
    if (type === 'files') return '\uE8B7';
    return '\uE8C8';
  }

  function searchInput(event: Event): void {
    onSearch((event.currentTarget as HTMLInputElement).value);
  }

  function trash(): void {
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

  function pin(event: MouseEvent, id: string): void {
    event.stopPropagation();
    onPin(id);
  }

  function rowClick(event: MouseEvent, id: string): void {
    onPaste(id, event.shiftKey);
  }

  function rowKey(event: KeyboardEvent, id: string): void {
    if (event.key !== 'Enter' && event.key !== ' ') return;
    event.preventDefault();
    onPaste(id, event.shiftKey);
  }
</script>

<div class="clip-tab">
  <div class="clip-bar">
    <span class="glyph clip-search-icon" aria-hidden="true">{'\uE721'}</span>
    <input
      class="clip-search"
      type="text"
      spellcheck="false"
      autocomplete="off"
      placeholder="Search your clipboard"
      aria-label="Search your clipboard"
      value={query}
      oninput={searchInput}
    />
    <button
      type="button"
      class="clip-trash"
      class:armed={confirmClear}
      title={confirmClear ? 'Click again to clear the history' : 'Clear the history'}
      aria-label={confirmClear ? 'Click again to clear the history' : 'Clear the history'}
      onclick={trash}
    >
      <span class="glyph">{confirmClear ? '\uE894' : '\uE74D'}</span>
      {#if confirmClear}<span class="clip-trash-label">Clear?</span>{/if}
    </button>
  </div>

  <div class="clip-list">
    {#if items.length === 0}
      <p class="empty">
        {query ? 'Nothing matches that search.' : 'Copy something and it shows up here. Click an item to paste it.'}
      </p>
    {:else}
      {#each items as item (item.id)}
        <div
          class="clip-row"
          class:pinned={item.pinned}
          role="button"
          tabindex="0"
          title="Click to paste · Shift+click to copy only"
          onclick={(event) => rowClick(event, item.id)}
          onkeydown={(event) => rowKey(event, item.id)}
        >
          <span class="clip-thumb">
            {#if item.thumb}
              <img src={`https://island.clips/${item.thumb}`} alt="" draggable="false" />
            {:else}
              <span class="glyph">{typeGlyph(item.type)}</span>
            {/if}
          </span>

          <span class="clip-text">
            <span class="clip-preview">{item.preview}</span>
            <span class="clip-meta">
              {item.source || 'Unknown app'} · {item.age}{item.chars > 0 ? ` · ${item.chars} chars` : ''}
            </span>
          </span>

          <button
            type="button"
            class="clip-pin"
            class:on={item.pinned}
            title={item.pinned ? 'Unpin' : 'Pin'}
            aria-label={item.pinned ? 'Unpin' : 'Pin'}
            onclick={(event) => pin(event, item.id)}
          >
            <span class="glyph">{'\uE718'}</span>
          </button>
        </div>
      {/each}
    {/if}
  </div>

  <p class="clip-hint">Click to paste · Shift+click to copy only</p>
</div>
