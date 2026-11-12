import { beforeEach } from 'vitest'

// A URL agora guarda a seção e os filtros: cada teste começa na Visão Geral, como uma abertura nova do site.
// (Os testes de layout rodam em ambiente node, sem window.)
beforeEach(() => { if (typeof window !== 'undefined') window.history.replaceState(null, '', '/') })
