export interface DashboardFilterProps {
  competence: string; months: number
  onCompetenceChange: (value: string) => void; onMonthsChange: (value: number) => void
}

export function DashboardFilters({ competence, months, onCompetenceChange, onMonthsChange }: DashboardFilterProps) {
  return <div className="dashboard-filters"><label>Competência<input aria-label="Competência do dashboard" type="month" required value={competence} onChange={event => { if (/^\d{4}-\d{2}$/.test(event.target.value)) onCompetenceChange(event.target.value) }} /></label><label>Horizonte<select aria-label="Horizonte da projeção" value={months} onChange={event => onMonthsChange(Number.parseInt(event.target.value, 10))}><option value={3}>3 meses</option><option value={6}>6 meses</option><option value={12}>12 meses</option></select></label></div>
}
