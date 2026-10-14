# Laboratório T09.3.8

Ambiente isolado para ensaiar `file → LLDAP → file`. Usa somente dados fictícios, portas locais `17171`, `3891` e
`19091`, diretório `runtime/` ignorado pelo Git e imagens fixadas por versão/digest (LLDAP de 2026-07-19 e Authelia
`4.39.26`).

Execute a partir da raiz pelo Git Bash/Linux:

```bash
bash scripts/t09.3.8/lab-test.sh
```

No WSL usando o Docker Desktop do Windows:

```bash
T09_DOCKER_BIN=docker.exe bash scripts/t09.3.8/lab-test.sh
```

O teste cria segredos aleatórios sem mostrá-los, provisiona `diego`, verifica o grupo `home-admin`, exercita os dois
backends sobre o mesmo SQLite do Authelia, compara o identificador opaco e comprova backup/restauração do LLDAP. Não
usa nem conhece caminhos, containers ou dados de produção.
