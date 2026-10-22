import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, expect, it } from 'vitest'
import { MonthlyFlowSummary } from './MonthlyFlowSummary'

afterEach(cleanup)
it('uses only provided realized values, with a labelled donut and equivalent text', () => {
  const { container } = render(<MonthlyFlowSummary income="5000.00" expense="2000.00" />)
  expect(screen.getByRole('heading', { name: 'Resumo do mês' })).toBeTruthy()
  expect(screen.getByRole('img', { name: /Composição/ })).toBeTruthy()
  expect(screen.getByText(/5\.000,00/)).toBeTruthy(); expect(screen.getByText(/2\.000,00/)).toBeTruthy()
  expect(container.querySelector('[stroke-dasharray]')).not.toBeNull()
  expect(screen.getByText(/Não representa saldo/)).toBeTruthy()
})
it('renders zero as known zero without fictional segments', () => {
  const { container } = render(<MonthlyFlowSummary income="0.00" expense="0.00" />)
  expect(screen.getByRole('img', { name: 'Sem movimentação realizada' })).toBeTruthy()
  expect(container.querySelector('[stroke-dasharray]')).toBeNull()
})
it('does not substitute missing values with zero or a fake chart', () => {
  const { container } = render(<MonthlyFlowSummary income={null} expense={null} />)
  expect(screen.getAllByText('Indisponível')).toHaveLength(2)
  expect(screen.queryByText(/R\$/)).toBeNull()
  expect(container.querySelector('[stroke-dasharray]')).toBeNull()
})
