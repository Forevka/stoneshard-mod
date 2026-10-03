import {themes as prismThemes} from 'prism-react-renderer';
import type {Config} from '@docusaurus/types';
import type * as Preset from '@docusaurus/preset-classic';

const repo = 'https://github.com/Forevka/stoneshard-mod';

const config: Config = {
  title: 'Lodestone',
  tagline: 'C# mods for any YYC-compiled GameMaker game',
  favicon: 'img/favicon.svg',

  url: 'https://forevka.github.io',
  baseUrl: '/stoneshard-mod/',
  organizationName: 'Forevka',
  projectName: 'stoneshard-mod',
  trailingSlash: false,

  onBrokenLinks: 'throw',
  onBrokenAnchors: 'throw',
  markdown: {
    mermaid: true,
    hooks: {onBrokenMarkdownLinks: 'throw'},
  },
  themes: [
    '@docusaurus/theme-mermaid',
    [
      '@easyops-cn/docusaurus-search-local',
      {hashed: true, docsRouteBasePath: '/', indexBlog: false, highlightSearchTermsOnTargetPage: true},
    ],
  ],

  i18n: {defaultLocale: 'en', locales: ['en']},

  presets: [
    [
      'classic',
      {
        docs: {
          routeBasePath: '/',
          sidebarPath: './sidebars.ts',
          editUrl: `${repo}/edit/main/docs-site/`,
        },
        blog: false,
        theme: {customCss: './src/css/custom.css'},
      } satisfies Preset.Options,
    ],
  ],

  themeConfig: {
    colorMode: {respectPrefersColorScheme: true},
    navbar: {
      title: 'Lodestone',
      logo: {alt: 'Lodestone', src: 'img/favicon.svg'},
      items: [
        {type: 'docSidebar', sidebarId: 'modding', position: 'left', label: 'Writing a mod'},
        {type: 'docSidebar', sidebarId: 'internals', position: 'left', label: 'Loader internals'},
        {href: `${repo}/blob/main/CHANGELOG.md`, label: 'Changelog', position: 'right'},
        {href: repo, label: 'GitHub', position: 'right'},
      ],
    },
    footer: {
      style: 'dark',
      links: [
        {
          title: 'Docs',
          items: [
            {label: 'Getting started', to: '/modding/getting-started'},
            {label: 'Cookbook', to: '/modding/cookbook'},
            {label: 'Loader internals', to: '/internals/architecture'},
          ],
        },
        {
          title: 'Project',
          items: [
            {label: 'GitHub', href: repo},
            {label: 'Installing (players)', href: `${repo}/blob/main/INSTALL.md`},
            {label: 'Changelog', href: `${repo}/blob/main/CHANGELOG.md`},
          ],
        },
      ],
      copyright: 'Lodestone (CoreLoader). MIT licence.',
    },
    prism: {
      theme: prismThemes.github,
      darkTheme: prismThemes.dracula,
      additionalLanguages: ['csharp', 'cpp', 'powershell', 'nasm', 'json', 'python', 'java'],
    },
  } satisfies Preset.ThemeConfig,
};

export default config;
