import './app.css';
import { mount } from 'svelte';
import App from './App.svelte';

const target = document.getElementById('app');
if (target === null) {
  throw new Error('AppLock Sync: #app mount point is missing from index.html');
}

mount(App, { target });
