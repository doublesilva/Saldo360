import { cleanup, render, screen, waitFor } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import { CardKnownCommitments } from './CardKnownCommitments'
import { getCardPortfolio, type CardPortfolioItem } from '../api/creditCards'
vi.mock('../api/creditCards', () => ({ getCardPortfolio: vi.fn() }))
afterEach(() => { cleanup(); vi.resetAllMocks() })
const card: CardPortfolioItem = { cardId: 'card', isArchived: true, creditLimit: '100.00', usedLimit: '20.00', availableLimit: '80.00', utilizationPercentage: '20.00', invoiceTotal: '0.00', unlinkedKnownCount: 1, unlinkedKnownAmount: '20.00', unlinkedPeriodAmount: '20.00', knownPeriodTotal: '20.00', outstandingPeriodAmount: '20.00', paidPeriodAmount: '0.00', unassignedCompetenceCount: 0, incompleteDetailsCount: 1, unlinkedObligations: [{ id: 'known', description: 'Known synthetic', amount: '20.00', competence: '2030-11-01', dueDate: null, number: null, count: null, sourceSystem: 'Synthetic', externalReference: 'source:1' }] }
it('consulta seis meses, preserva arquivados e sinaliza complementos ausentes sem oferecer edição', async () => {
  vi.mocked(getCardPortfolio).mockResolvedValue({ cardCount: 0, totalLimit: '0.00', invoiceTotal: '0.00', availableLimit: '0.00', cards: [card] })
  render(<CardKnownCommitments workspaceId="workspace" cardId="card" month="2030-11" />)
  await screen.findAllByText(/Known synthetic/)
  expect(getCardPortfolio).toHaveBeenCalledTimes(6)
  expect(vi.mocked(getCardPortfolio).mock.calls.map(c => c[1])).toEqual(['2030-11', '2030-12', '2031-01', '2031-02', '2031-03', '2031-04'])
  expect(screen.getAllByText(/Cobertura parcial de informações/)).toHaveLength(6)
  expect(screen.getAllByText(/Cartão arquivado/)).toHaveLength(6)
  expect(screen.queryByRole('button')).toBeNull()
  expect(Array.from(document.querySelectorAll('details')).map(d => d.open)).toEqual([true, false, false, false, false, false])
})
it('diferencia zero confirmado de indisponibilidade e remove resultados do contexto anterior', async () => {
  vi.mocked(getCardPortfolio).mockResolvedValue({ cardCount: 1, totalLimit: '100.00', invoiceTotal: '0.00', availableLimit: '100.00', cards: [{ ...card, isArchived: false, unlinkedPeriodAmount: '0.00', knownPeriodTotal: '0.00', outstandingPeriodAmount: '0.00', incompleteDetailsCount: 0, unlinkedObligations: [] }] })
  const view = render(<CardKnownCommitments workspaceId="one" cardId="card" month="2030-11" />)
  await screen.findAllByText(/Total conhecido/)
  expect(screen.queryByRole('alert')).toBeNull()
  vi.mocked(getCardPortfolio).mockRejectedValue(new Error('Unavailable'))
  view.rerender(<CardKnownCommitments workspaceId="two" cardId="card" month="2030-11" />)
  await screen.findByRole('alert')
  await waitFor(() => expect(screen.queryByText(/Total conhecido/)).toBeNull())
})
