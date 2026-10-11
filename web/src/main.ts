import { mount } from 'svelte'
import App from './App.svelte'
// Bundled, so they work with no internet at the scope.
import '@fontsource-variable/inter'
import '@fontsource-variable/jetbrains-mono'
import './app.css'

export default mount(App, { target: document.getElementById('app')! })
