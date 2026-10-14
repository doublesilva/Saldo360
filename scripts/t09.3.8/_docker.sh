#!/usr/bin/env bash

# Permite que o mesmo ensaio seja executado no Linux ou via WSL contra o
# Docker Desktop do Windows. O padrão de produção continua sendo `docker`.
docker_cli() {
  "${T09_DOCKER_BIN:-docker}" "$@"
}
