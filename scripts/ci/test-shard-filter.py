#!/usr/bin/env python3
"""Imprime o filtro `dotnet test --filter` da fatia N (1..K) de scripts/ci/test-shards.json.

Fatia comum: OU dos nomes listados (FullyQualifiedName~). Ultima fatia: as suas unidades OU a negacao de TODAS as unidades listadas em qualquer fatia, de modo que
nenhum teste fica sem fatia (teste novo/renomeado cai na ultima). Uso: python3 scripts/ci/test-shard-filter.py 3
"""
import json, os, sys

PREFIX = 'Saldo360.Tests.'  # o FQN sempre contem o prefixo; `~` e "contem", entao omiti-lo encurta o filtro sem mudar a selecao
short = lambda name: name[len(PREFIX):] if name.startswith(PREFIX) else name
config = json.load(open(os.path.join(os.path.dirname(__file__), 'test-shards.json'), encoding='utf-8'))
shards = config['shards']
index = int(sys.argv[1]) - 1
if not 0 <= index < len(shards):
    sys.exit(f'fatia invalida: {sys.argv[1]} (1..{len(shards)})')
own = '|'.join(f'FullyQualifiedName~{short(name)}' for name in shards[index]['include'])
if shards[index].get('rest'):
    everything = [name for shard in shards for name in shard['include']]
    unlisted = '&'.join(f'FullyQualifiedName!~{short(name)}' for name in everything)
    print(f'({own})|({unlisted})')
else:
    print(own)
