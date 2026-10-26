import { mount } from 'svelte'
import Root from './Root.svelte'
// Bundled, so they work with no internet at the scope.
import '@fontsource-variable/inter'
import '@fontsource-variable/jetbrains-mono'
import './app.css'

export default mount(Root, { target: document.getElementById('app')! })
