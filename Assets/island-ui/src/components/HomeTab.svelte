<script lang="ts">
  import type { DeviceItem, HomeStatsState, MediaState, RecentItem } from '../motion';

  export let media: MediaState;
  export let stats: HomeStatsState;
  export let recent: RecentItem[];
  export let devices: DeviceItem[];
  export let onTile: (id: string) => void;
  export let onMedia: (command: string) => void;
  export let onVolume: (percent: number) => void;
  export let onDevice: (id: string) => void;
  export let onOpenClipboard: () => void;

  // The eight Home tiles (island.md 7). Icons are Segoe Fluent glyphs, labels are our own words.
  const TILES = [
    { id: 'area', label: 'Area', icon: '\uE7A8', title: 'Select an area to capture (Win + Shift + S)' },
    { id: 'fullscreen', label: 'Full screen', icon: '\uE722', title: 'Capture this monitor and copy it' },
    { id: 'screentext', label: 'Screen text', icon: '\uE8D2', title: 'Copy text from the screen (Win + Shift + T)' },
    { id: 'qr', label: 'QR code', icon: '\uE9F9', title: 'Scan a QR code on this monitor' },
    { id: 'network', label: 'Network', icon: '\uE774', title: 'Download and upload speed' },
    { id: 'cpuram', label: 'CPU & RAM', icon: '\uE950', title: 'Processor and memory load' },
    { id: 'record', label: 'Record', icon: '\uE7C8', title: 'Record the screen (Win + Shift + R)' },
    { id: 'command', label: 'Command', icon: '\uE756', title: 'Open the command bar' },
  ];

  $: progress = media.duration > 0 ? Math.min(1, Math.max(0, media.position / media.duration)) : 0;
  $: currentDevice = devices.find((device) => device.isDefault);
  $: visibleRecent = recent.slice(0, media.hasSession ? 2 : 3);

  /** ``4.2M`` reads better than a full unit in a tile this narrow. */
  function compact(bytesPerSec: number): string {
    if (!Number.isFinite(bytesPerSec) || bytesPerSec < 1) return '0';
    const units = ['B', 'K', 'M', 'G'];
    let value = bytesPerSec;
    let index = 0;
    while (value >= 1024 && index < units.length - 1) {
      value /= 1024;
      index++;
    }
    return `${value < 10 ? value.toFixed(1) : Math.round(value)}${units[index]}`;
  }

  function tileValue(id: string): string {
    switch (id) {
      case 'network':
        return `\u2193${compact(stats.down)} \u2191${compact(stats.up)}`;
      case 'cpuram':
        return `${Math.round(stats.cpu)}% / ${Math.round(stats.ram)}%`;
      default:
        return '';
    }
  }

  function onVolumeInput(event: Event): void {
    onVolume(Number((event.currentTarget as HTMLInputElement).value));
  }

  function onDeviceChange(event: Event): void {
    onDevice((event.currentTarget as HTMLSelectElement).value);
  }
</script>

<div class="home">
  <div class="home-main">
    {#if media.hasSession}
      <div class="player">
        <div class="art">
          {#if media.art}
            <img src={`https://island.cache/${media.art}`} alt="" draggable="false" />
          {:else}
            <span class="glyph art-glyph" aria-hidden="true">{'\uE8B8'}</span>
          {/if}
        </div>

        <div class="player-text">
          <span class="player-title" title={media.title}>{media.title || 'Nothing playing'}</span>
          <span class="player-artist">{(media.artist || media.app).toUpperCase()}</span>
          <div class="progress" aria-hidden="true">
            <div class="progress-fill" style={`transform:scaleX(${progress})`}></div>
          </div>
        </div>

        <div class="player-controls">
          <button type="button" class="icon-btn" title="Previous" onclick={() => onMedia('previous')}>
            <span class="glyph">{'\uE892'}</span>
          </button>
          <button
            type="button"
            class="icon-btn"
            title={media.playing ? 'Pause' : 'Play'}
            onclick={() => onMedia('toggle')}
          >
            <span class="glyph">{media.playing ? '\uE769' : '\uE768'}</span>
          </button>
          <button type="button" class="icon-btn" title="Next" onclick={() => onMedia('next')}>
            <span class="glyph">{'\uE893'}</span>
          </button>
        </div>
      </div>
    {/if}

    <div class="recent">
      <div class="section-head">
        <span>Recently copied</span>
        <button type="button" class="link" title="Open the Clipboard tab" onclick={onOpenClipboard}>
          {'\uE76C'}
        </button>
      </div>

      {#if visibleRecent.length === 0}
        <p class="empty">Copy something and your latest items show up here.</p>
      {:else}
        {#each visibleRecent as item (item.preview + item.age)}
          <div class="recent-item">
            <span class="glyph recent-icon" aria-hidden="true">{item.kind.includes('image') ? '\uE91B' : '\uE8C8'}</span>
            <span class="recent-text" title={item.preview}>{item.preview}</span>
            <span class="recent-age">{item.age}</span>
          </div>
        {/each}
      {/if}
    </div>

    <div class="volume-row">
      <span class="glyph volume-icon" aria-hidden="true">{stats.volume === 0 ? '\uE74F' : '\uE767'}</span>
      <input
        class="slider"
        type="range"
        min="0"
        max="100"
        step="1"
        value={stats.volume}
        aria-label="Volume"
        oninput={onVolumeInput}
      />
      <span class="volume-value">{stats.volume}%</span>
      <select
        class="device"
        aria-label="Output device"
        title={currentDevice?.name ?? 'Output device'}
        value={currentDevice?.id ?? ''}
        onchange={onDeviceChange}
      >
        {#each devices as device (device.id)}
          <option value={device.id}>{device.name}</option>
        {/each}
      </select>
    </div>
  </div>

  <div class="tiles">
    {#each TILES as tile (tile.id)}
      <button type="button" class="tile" title={tile.title} onclick={() => onTile(tile.id)}>
        <span class="glyph tile-glyph" aria-hidden="true">{tile.icon}</span>
        <span class="tile-text">
          <span class="tile-label">{tile.label}</span>
          {#if tile.id === 'network' || tile.id === 'cpuram'}
            <span class="tile-value">{tileValue(tile.id)}</span>
          {/if}
        </span>
      </button>
    {/each}
  </div>
</div>
