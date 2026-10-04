import type {SidebarsConfig} from '@docusaurus/plugin-content-docs';

const sidebars: SidebarsConfig = {
  modding: [
    'index',
    'modding/getting-started',
    'modding/first-mod',
    'modding/concepts',
    'modding/interop',
    'modding/finding-hooks',
    {
      type: 'category',
      label: 'Cookbook',
      link: {type: 'doc', id: 'modding/cookbook/index'},
      items: [
        'modding/cookbook/hooks',
        'modding/cookbook/game-state',
        'modding/cookbook/calling-the-game',
        'modding/cookbook/drawing-and-ui',
        'modding/cookbook/input',
        'modding/cookbook/settings-and-persistence',
        'modding/cookbook/object-types',
        'modding/cookbook/robustness-and-testing',
      ],
    },
    {
      type: 'category',
      label: 'Walkthroughs',
      items: ['modding/walkthroughs/console', 'modding/walkthroughs/fast-travel'],
    },
    {
      type: 'category',
      label: 'Reference',
      items: [
        'modding/reference/api',
        'modding/reference/analyzers',
        'modding/reference/test-host',
        'modding/reference/shipped-mods',
        'modding/reference/troubleshooting',
      ],
    },
  ],
  internals: [
    'internals/architecture',
    'internals/boot',
    'internals/gml-functions',
    'internals/builtins',
    'internals/runtime-bridge',
    'internals/runtime-differences',
    'internals/hook-engine',
    'internals/object-types',
    'internals/dotnet-host',
    'internals/overlay',
    'internals/managed-runtime',
    'internals/re-toolkit',
    'internals/contributing',
  ],
};

export default sidebars;
