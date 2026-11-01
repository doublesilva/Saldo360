#!/usr/bin/env python3
"""Gera scripts/ci/test-shards.json a partir de um .trx de execucao completa (balanceamento por duracao medida).

Uso: python3 scripts/ci/make-test-shards.py full.trx [N] > scripts/ci/test-shards.json
A ultima fatia ("rest") executa as suas unidades E tudo que NAO esta listado em nenhuma fatia (filtro por negacao), entao um teste novo ou renomeado
cai nela em vez de se perder. A classe gigante PluggyFinancialIntegrationTests e dividida por metodo; as demais por classe.
"""
import collections, json, sys, xml.etree.ElementTree as ET

NS = '{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}'
BIG = 'Saldo360.Tests.Pluggy.PluggyFinancialIntegrationTests'

def main(path, n):
    units = collections.defaultdict(float)   # nome (classe ou classe.metodo) -> segundos
    for r in ET.parse(path).getroot().iter(NS + 'UnitTestResult'):
        h, m, s = r.get('duration').split(':')
        seconds = int(h) * 3600 + int(m) * 60 + float(s)
        name = r.get('testName').split('(')[0]
        cls = name.rsplit('.', 1)[0]
        units[name if cls == BIG else cls] += seconds
    shards = [{'seconds': 0.0, 'include': []} for _ in range(n)]
    for name, seconds in sorted(units.items(), key=lambda kv: -kv[1]):
        target = min(shards, key=lambda s: s['seconds'])
        target['include'].append(name)
        target['seconds'] += seconds
    total = sum(units.values())
    out = {'generatedFrom': 'trx de execucao completa', 'totalSeconds': round(total),
           'shards': [{'include': sorted(s['include']), 'seconds': round(s['seconds']), 'rest': i == n - 1} for i, s in enumerate(shards)]}
    print(json.dumps(out, indent=1))

if __name__ == '__main__':
    main(sys.argv[1], int(sys.argv[2]) if len(sys.argv) > 2 else 8)
