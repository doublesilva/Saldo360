const hints: Record<string, string> = {
  CpfNotRegistered: 'CPF não cadastrado. Informe a senha do PDF manualmente.',
  CpfInvalid: 'CPF do perfil inválido. Informe a senha do PDF manualmente.',
  CpfSessionInvalid: 'Sessão de perfil inválida ou expirada. Informe a senha manualmente ou autentique novamente.',
  CpfAccessDenied: 'Acesso ao perfil não autorizado. Informe a senha do PDF manualmente.',
  CpfUnavailable: 'Perfil temporariamente indisponível. Informe a senha do PDF manualmente.',
}
export function PdfPasswordHint({ code }: { code?: string | null }) {
  return code && hints[code] ? <p role="status">{hints[code]}</p> : null
}
