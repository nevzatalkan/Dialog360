// Tabler Icons (MIT) yollarından sadeleştirilmiş, 24x24 çizgi ikonlar
const base = { width: 20, height: 20, viewBox: '0 0 24 24', fill: 'none', stroke: 'currentColor', strokeWidth: 2, strokeLinecap: 'round', strokeLinejoin: 'round', 'aria-hidden': true } as const

export const MenuIcon = () => (
  <svg {...base}><path d="M4 6h16M4 12h16M4 18h16" /></svg>
)

export const TrashIcon = () => (
  <svg {...base} width={16} height={16}>
    <path d="M4 7h16M10 11v6M14 11v6M5 7l1 12a2 2 0 0 0 2 2h8a2 2 0 0 0 2-2l1-12M9 7V4a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v3" />
  </svg>
)

export const LogoutIcon = () => (
  <svg {...base} width={16} height={16}>
    <path d="M14 8V6a2 2 0 0 0-2-2H5a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2h7a2 2 0 0 0 2-2v-2M9 12h12l-3-3M18 15l3-3" />
  </svg>
)

export const PlusIcon = () => (
  <svg {...base} width={16} height={16}><path d="M12 5v14M5 12h14" /></svg>
)

export const CloseIcon = () => (
  <svg {...base} width={18} height={18}><path d="M18 6 6 18M6 6l12 12" /></svg>
)
