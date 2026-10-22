import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, expect, it } from 'vitest'
import { MonthlyFlowSummary } from './MonthlyFlowSummary'

afterEach(cleanup)
it('uses only provided realized values, with a labelled donut and equivalent text', () => {
  const { container } = render(<MonthlyFlowSummary expense="5000.00" pendingExpense="2000.00" />)
  expect(screen.getByRole('heading', { name: 'Resumo do mês' })).toBeTruthy()
  expect(screen.getByRole('img', { name: /Composição/ })).toBeTruthy()
  expect(screen.getByText(/5\.000,00/)).toBeTruthy(); expect(screen.getByText(/2\.000,00/)).toBeTruthy()
  expect(container.querySelector('[stroke-dasharray]')).not.toBeNull()
  expect(screen.getByText(/Não representa saldo/)).toBeTruthy()
  expect(screen.getByText(/71,4%/)).toBeTruthy()
  expect(screen.getByText(/28,6%/)).toBeTruthy()
})
it('renders zero as known zero without fictional segments', () => {
  const { container } = render(<MonthlyFlowSummary expense="0.00" pendingExpense="0.00" />)
  expect(screen.getByRole('img', { name: 'Sem despesas conhecidas' })).toBeTruthy()
  expect(container.querySelector('[stroke-dasharray]')).toBeNull()
})
it('does not substitute missing values with zero or a fake chart', () => {
  const { container } = render(<MonthlyFlowSummary expense={null} pendingExpense={null} />)
  expect(screen.getAllByText(/Indisponível/)).toHaveLength(2)
  expect(screen.queryByText(/R\$/)).toBeNull()
  expect(container.querySelector('[stroke-dasharray]')).toBeNull()
})
