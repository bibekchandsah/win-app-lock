<script lang="ts">
  import { firebaseConfigState } from './lib/firebase';
  import {
    applyTheme,
    readStoredTheme,
    storeTheme,
    themeLabel,
    watchSystemTheme,
    type ThemeMode,
  } from './lib/theme';

  const modes: readonly ThemeMode[] = ['system', 'light', 'dark'];
  const config = firebaseConfigState();

  let mode = $state<ThemeMode>(readStoredTheme());

  $effect(() => {
    applyTheme(mode);
    storeTheme(mode);
    return watchSystemTheme(() => applyTheme(mode));
  });

  const tabs = [
    { name: 'Overview', detail: 'Lock, sleep, restart, shut down, live performance' },
    { name: 'Audio', detail: 'Speaker and microphone, per-app mixer, media sessions' },
    { name: 'Display', detail: 'Brightness per monitor, blur effect' },
    { name: 'Network', detail: 'Radios, Wi-Fi schedule, LAN devices, open ports, website lists' },
    { name: 'Apps', detail: 'Running and protected apps, keyboard, screen time' },
    { name: 'Content', detail: 'Notes, reminders, clipboard (end-to-end encrypted)' },
  ] as const;
</script>

<div class="shell">
  <header class="topbar">
    <div class="brand">
      <span class="brand-dot" aria-hidden="true"></span>
      <span class="brand-name">AppLock Sync</span>
      <span class="brand-tag">dashboard</span>
    </div>

    <div class="theme-switch" role="group" aria-label="Theme">
      {#each modes as option (option)}
        <button
          type="button"
          class="theme-option"
          class:active={mode === option}
          aria-pressed={mode === option}
          title={themeLabel(option)}
          onclick={() => (mode = option)}
        >
          {themeLabel(option)}
        </button>
      {/each}
    </div>
  </header>

  <main>
    <section class="hero card">
      <p class="eyebrow">Milestone 0 · foundation</p>
      <h1>Your PC, from anywhere — without handing the cloud your data.</h1>
      <p class="lede">
        This is the web dashboard shell for AppLock Sync. Sign-in, device pairing and the live
        control pages arrive in the next milestones; security rules, emulator tests and the project
        layout are already in place.
      </p>

      <dl class="facts">
        <div class="fact">
          <dt>Firebase project</dt>
          <dd>{config.configured ? config.projectId : 'not configured yet'}</dd>
        </div>
        <div class="fact">
          <dt>Database rules</dt>
          <dd>owner-only, email-verified, schema-validated</dd>
        </div>
        <div class="fact">
          <dt>Content encryption</dt>
          <dd>AES-256-GCM, keys derived on your device</dd>
        </div>
      </dl>

      {#if !config.configured}
        <p class="notice">
          Copy <code>.env.example</code> to <code>.env.local</code> and fill in the public Firebase
          web config (project id, API key, database URL, app id) to point this dashboard at a
          project or the emulator.
        </p>
      {/if}
    </section>

    <section class="card">
      <h2>What the dashboard will control</h2>
      <ul class="tabs">
        {#each tabs as tab (tab.name)}
          <li>
            <span class="tab-name">{tab.name}</span>
            <span class="tab-detail">{tab.detail}</span>
          </li>
        {/each}
      </ul>
    </section>

    <section class="card security">
      <h2>How it stays safe</h2>
      <ul class="checks">
        <li>Every permission is decided on the PC, never by a field in the database.</li>
        <li>Risky commands need a proof derived from your Sync passphrase; the most dangerous ones add a step-up and a cancel window.</li>
        <li>Notes, clipboard, app names and device names leave the PC encrypted; only small status fields are readable.</li>
        <li>This page never stores your passphrase or keys — they live in memory for the session only.</li>
        <li>No service account, admin SDK or refresh token is ever shipped to the browser.</li>
      </ul>
      <p class="limits">
        Honest limits: the PC must be on, awake, signed in and running AppLock. A sleeping PC is
        offline and commands expire. No remote screen, keyboard, files, or Vault access — ever.
      </p>
    </section>
  </main>

  <footer class="foot">
    <span>AppLock Sync · no analytics, no third-party scripts</span>
    <span>Docs: docs/threat-model.md · docs/firebase-setup.md</span>
  </footer>
</div>

<style>
  .shell {
    min-height: 100dvh;
    display: flex;
    flex-direction: column;
    gap: 24px;
    padding: 20px clamp(16px, 4vw, 48px) 32px;
    max-width: 1120px;
    margin: 0 auto;
  }

  .topbar {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 16px;
    flex-wrap: wrap;
  }

  .brand {
    display: flex;
    align-items: center;
    gap: 10px;
    font-weight: 600;
  }

  .brand-dot {
    width: 12px;
    height: 12px;
    border-radius: 999px;
    background: var(--accent);
    box-shadow: 0 0 0 4px color-mix(in srgb, var(--accent) 22%, transparent);
  }

  .brand-name {
    font-size: 15px;
    letter-spacing: 0.2px;
  }

  .brand-tag {
    font-size: 11px;
    text-transform: uppercase;
    letter-spacing: 0.12em;
    color: var(--text-muted);
  }

  .theme-switch {
    display: inline-flex;
    padding: 3px;
    gap: 2px;
    border-radius: 999px;
    border: 1px solid var(--border);
    background: var(--surface-2);
  }

  .theme-option {
    border: 0;
    background: transparent;
    color: var(--text-muted);
    font: inherit;
    font-size: 12px;
    padding: 6px 12px;
    border-radius: 999px;
    cursor: pointer;
  }

  .theme-option:hover {
    color: var(--text);
    background: var(--surface-hover);
  }

  .theme-option.active {
    background: var(--accent-soft);
    color: var(--accent-strong);
  }

  main {
    display: flex;
    flex-direction: column;
    gap: 18px;
  }

  .card {
    border: 1px solid var(--border);
    background: var(--surface);
    border-radius: 16px;
    padding: clamp(16px, 2.4vw, 26px);
  }

  .hero h1 {
    margin: 6px 0 10px;
    font-size: clamp(22px, 3.2vw, 32px);
    line-height: 1.2;
    letter-spacing: -0.01em;
  }

  .eyebrow {
    margin: 0;
    font-size: 11px;
    text-transform: uppercase;
    letter-spacing: 0.14em;
    color: var(--accent-strong);
  }

  .lede {
    margin: 0;
    max-width: 68ch;
    color: var(--text-muted);
    line-height: 1.6;
  }

  .facts {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(190px, 1fr));
    gap: 12px;
    margin: 20px 0 0;
  }

  .fact {
    border: 1px solid var(--border);
    border-radius: 12px;
    padding: 12px 14px;
    background: var(--surface-2);
  }

  .fact dt {
    font-size: 11px;
    text-transform: uppercase;
    letter-spacing: 0.1em;
    color: var(--text-muted);
  }

  .fact dd {
    margin: 6px 0 0;
    font-size: 13.5px;
    line-height: 1.4;
  }

  h2 {
    margin: 0 0 12px;
    font-size: 15px;
    font-weight: 600;
  }

  .tabs {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(240px, 1fr));
    gap: 10px;
  }

  .tabs li {
    display: flex;
    flex-direction: column;
    gap: 4px;
    padding: 12px 14px;
    border-radius: 12px;
    background: var(--surface-2);
    border: 1px solid var(--border);
  }

  .tab-name {
    font-size: 13px;
    font-weight: 600;
  }

  .tab-detail {
    font-size: 12.5px;
    color: var(--text-muted);
    line-height: 1.45;
  }

  .checks {
    margin: 0;
    padding-left: 18px;
    display: flex;
    flex-direction: column;
    gap: 8px;
    font-size: 13.5px;
    line-height: 1.5;
  }

  .checks li::marker {
    color: var(--accent);
  }

  .limits {
    margin: 14px 0 0;
    font-size: 12.5px;
    color: var(--text-muted);
    line-height: 1.55;
    border-left: 2px solid var(--accent-soft);
    padding-left: 12px;
  }

  .notice {
    margin: 16px 0 0;
    font-size: 13px;
    line-height: 1.55;
    color: var(--text);
    background: var(--accent-soft);
    border-radius: 10px;
    padding: 10px 12px;
  }

  code {
    font-family: var(--mono);
    font-size: 0.92em;
  }

  .foot {
    margin-top: auto;
    display: flex;
    justify-content: space-between;
    gap: 12px;
    flex-wrap: wrap;
    font-size: 11.5px;
    color: var(--text-muted);
  }

  @media (max-width: 520px) {
    .foot {
      flex-direction: column;
    }
  }
</style>
