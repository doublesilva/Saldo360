# Diretriz de automação do Saldo360

Diretriz permanente de produto e de arquitetura. Vale para toda nova especificação, decisão e implementação (inclusive de agentes de desenvolvimento).

Todas as rotinas operacionais e financeiras funcionam automaticamente, sem depender de análise, confirmação ou intervenção manual do usuário no fluxo normal. O usuário usa o Saldo360 para acompanhar sua situação financeira e decidir estrategicamente, não para conciliar nem executar tarefas repetitivas.

## Princípios obrigatórios

1. **Automação completa**: importação, categorização, conciliação, identificação de pagamentos, atualização de valores reais, recorrências e projeções funcionam sozinhas.
2. **Decisão baseada em evidências**: usar dados bancários, históricos, recorrências, regras financeiras e vínculos anteriores para decidir com segurança.
3. **Tratamento automático de exceções**: diante de divergência ou ambiguidade, investigar os dados disponíveis antes de pedir qualquer intervenção.
4. **Diagnóstico detalhado**: sem decisão segura, registrar o problema, os registros envolvidos, as hipóteses, os riscos e as soluções possíveis.
5. **Aprendizado das exceções**: transformar os casos identificados em regras que resolvam automaticamente casos semelhantes.
6. **Integridade financeira**: nunca criar despesas duplicadas, confirmar pagamentos sem evidência nem alterar valores com base em suposições inseguras.
7. **Intervenção humana excepcional**: só quando não houver evidência suficiente; nesses casos, apresentar alternativas fundamentadas, não exigir investigação manual.

## Como aplicar ao projetar ou implementar

- Prefira evoluir o motor de automação (regras, evidências, histórico) a criar uma nova etapa manual.
- Uma ação manual só entra como exceção, depois que o motor registrou o diagnóstico (candidatos, motivo, impacto) e não pôde decidir com segurança.
- Automação nunca relaxa as regras de segurança financeira: correspondência por valor ou descrição genérica isolada não é evidência; ambiguidade não vira associação.
- Toda atualização automática relevante é rastreável (origem, referência, valor anterior e novo) e idempotente; confirmação manual nunca é sobrescrita por rotina automática.

Exemplo de referência: PROJ-05 ([confirmação automática do valor real](PROJ-05-confirmacao-automatica-pluggy.md)).
