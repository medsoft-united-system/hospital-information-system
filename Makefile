.DEFAULT_GOAL := help

PYTHON ?= $(if $(wildcard /opt/homebrew/bin/python3),/opt/homebrew/bin/python3,python3)
DOTNET ?= dotnet
DOCKER ?= docker
ISU_ID ?= 408944
SECURITY_DIR ?= $(HOME)/.local/share/MedSoft/development-security/$(ISU_ID)
TRUST_CA ?= 1
NO_BUILD ?= 0
INTEGRATION_SECURITY_DIR ?= $(SECURITY_DIR)-lab3
LAB3_NETWORK ?= medsoft-lab3
LAB3_REPORT ?= artifacts/lab3/integration-report.json
LIS_SOURCE ?=
LIS_REPO ?= $(abspath ../laboratory-information-system)
FRESH_DATABASE ?= 0
LAB4_SECURITY_DIR ?= $(SECURITY_DIR)-lab4
MLLP_PORT ?= 2575
HL7_CLIENT_CA ?=

quote = '$(subst ','"'"',$(1))'
build_flag = $(if $(filter 1,$(NO_BUILD)),--no-build,)
fresh_database_flag = $(if $(filter 1,$(FRESH_DATABASE)),--fresh-database,)
lis_build_flag = $(if $(filter 1,$(NO_BUILD)),,--build)
lis_compose = HIS_CA_FILE=$(call quote,$(INTEGRATION_SECURITY_DIR)/lis-ca-cert.pem) LAB3_NETWORK=$(call quote,$(LAB3_NETWORK)) $(call quote,$(DOCKER)) compose --project-directory $(call quote,$(LIS_REPO)) -f $(call quote,$(LIS_REPO)/compose.yaml) -f $(call quote,$(CURDIR)/compose.lis-integration.yaml)
client_ca_flag = $(if $(strip $(HL7_CLIENT_CA)),--client-ca $(call quote,$(HL7_CLIENT_CA)),)
lab4_flags = --security-directory $(call quote,$(SECURITY_DIR)) --integration-security-directory $(call quote,$(INTEGRATION_SECURITY_DIR)) --output-directory $(call quote,$(LAB4_SECURITY_DIR)) --docker-network $(call quote,$(LAB3_NETWORK)) --mllp-port $(call quote,$(MLLP_PORT)) $(client_ca_flag)
trust_flag = $(if $(filter 1,$(TRUST_CA)),--trust-ca,)
lis_source_flag = $(if $(strip $(LIS_SOURCE)),--lis-source $(call quote,$(LIS_SOURCE)),)

.PHONY: help run demo db db-stop build test restore setup certificates prepare lis-up lis-stop lis-status lis-logs lab3-check lab3-report test-unit test-integration test-scripts lab3-prepare lab3-run lab3-smoke lab4-prepare lab4-run lab4-check

help:
	@printf '%s\n' \
	  'make prepare   — восстановить инструменты, подготовить БД и сертификаты' \
	  'make run       — запустить PostgreSQL, API и Web с рабочими данными' \
	  'make demo      — одноразовый стенд для настройки администратора' \
	  'make db        — запустить только PostgreSQL и дождаться готовности' \
	  'make db-stop   — остановить PostgreSQL с сохранением данных' \
	  'make build     — собрать solution' \
	  'make test      — выполнить C#-тесты и проверки Python-скриптов' \
	  'make test-unit / test-integration / test-scripts — отдельные проверки' \
	  'make lab3-prepare — подготовить TLS his-api с сохранением рабочих ключей' \
	  'make lab3-run  — рабочая HIS в отдельной сети для подключения Go LIS' \
	  'make lis-up    — запустить LIS и подключить её к сети HIS' \
	  'make lis-stop / lis-status / lis-logs — управление LIS' \
	  'make lab3-check — совместная проверка HIS и LIS' \
	  'make lab3-report — показать отчёт проверки' \
	  'make lab3-smoke — изолированная HTTPS-проверка лабораторной 3' \
	  'make lab4-prepare — подготовить MLLP/mTLS и client certificate, сохранив ключи HIS' \
	  'make lab4-run — запустить HIS с HTTPS и MLLP для desktop, сохранив LIS' \
	  'make lab4-check — проверить launcher, TLS и upgrade на одноразовых данных' \
	  '' \
	  'Параметры: ISU_ID=408944, SECURITY_DIR=/path/to/security, NO_BUILD=1.' \
	  'make demo TRUST_CA=0 — не добавлять CA в связку ключей повторно.' \
	  'LIS_REPO=/path/to/laboratory-information-system — путь к LIS.' \
	  'make prepare FRESH_DATABASE=1 — первоначальная настройка новой БД.' \
	  'LIS_SOURCE=/path/to/lis — дополнительно проверить закреплённый Go-клиент.' \
	  'Docker Desktop должен работать. Остановка приложения: Ctrl+C.'

run:
	@$(call quote,$(PYTHON)) scripts/run-application.py --start-database \
	  --security-directory $(call quote,$(SECURITY_DIR)) --docker $(call quote,$(DOCKER)) $(build_flag)

demo:
	@$(call quote,$(PYTHON)) scripts/run-setup-demo.py --isu-id $(call quote,$(ISU_ID)) \
	  --docker $(call quote,$(DOCKER)) $(trust_flag) $(build_flag)

lab3-prepare:
	@$(call quote,$(PYTHON)) scripts/prepare-lab3-integration.py \
	  --security-directory $(call quote,$(SECURITY_DIR)) \
	  --output-directory $(call quote,$(INTEGRATION_SECURITY_DIR)) \
	  --docker-network $(call quote,$(LAB3_NETWORK))

lab3-run:
	@$(call quote,$(PYTHON)) scripts/prepare-lab3-integration.py --run --start-database \
	  --security-directory $(call quote,$(SECURITY_DIR)) \
	  --output-directory $(call quote,$(INTEGRATION_SECURITY_DIR)) \
	  --docker-network $(call quote,$(LAB3_NETWORK)) --docker $(call quote,$(DOCKER)) $(build_flag)

lab4-prepare:
	@$(call quote,$(PYTHON)) scripts/prepare-lab4-integration.py $(lab4_flags)

lab4-run:
	@$(call quote,$(PYTHON)) scripts/prepare-lab4-integration.py $(lab4_flags) --run --start-database \
	  --docker $(call quote,$(DOCKER)) --dotnet $(call quote,$(DOTNET)) $(build_flag)

lab4-check:
	@$(call quote,$(PYTHON)) -m unittest discover -s scripts/tests
	@$(call quote,$(PYTHON)) scripts/verify-lab4-launcher.py --isu-id $(call quote,$(ISU_ID)) \
	  --docker $(call quote,$(DOCKER)) $(build_flag)
	@$(call quote,$(PYTHON)) scripts/verify-lab4-transport.py --docker $(call quote,$(DOCKER)) --include-upgrade

lab3-smoke:
	@$(call quote,$(PYTHON)) scripts/verify-lab3-integration.py --isu-id $(call quote,$(ISU_ID)) \
	  --report $(call quote,$(LAB3_REPORT)) --docker $(call quote,$(DOCKER)) $(lis_source_flag) $(build_flag)

db:
	@$(call quote,$(DOCKER)) compose --file docker-compose.yml up -d --wait postgres

db-stop:
	@$(call quote,$(DOCKER)) compose --file docker-compose.yml stop postgres

build:
	@$(call quote,$(DOTNET)) build HospitalInformationSystem.slnx

test:
	@$(call quote,$(DOTNET)) test HospitalInformationSystem.slnx
	@$(call quote,$(PYTHON)) -m unittest discover -s scripts/tests

restore:
	@$(call quote,$(DOTNET)) restore HospitalInformationSystem.slnx
	@$(call quote,$(DOTNET)) tool restore

setup:
	@$(call quote,$(PYTHON)) scripts/setup-development.py $(fresh_database_flag)

certificates:
	@if test -f $(call quote,$(SECURITY_DIR)/development-security.json); then \
	  printf '%s\n' 'Используется существующий выпуск сертификатов.'; \
	else \
	  $(call quote,$(PYTHON)) scripts/generate-development-certificates.py --isu-id $(call quote,$(ISU_ID)) --output-dir $(call quote,$(SECURITY_DIR)); \
	fi

prepare: restore setup certificates

lis-up: lab3-prepare
	@$(lis_compose) up -d $(lis_build_flag)

lis-stop:
	@$(lis_compose) stop

lis-status:
	@$(lis_compose) ps

lis-logs:
	@$(lis_compose) logs --tail 100 -f lis

lab3-check: LIS_SOURCE = $(LIS_REPO)
lab3-check: lab3-smoke

lab3-report:
	@$(call quote,$(PYTHON)) -m json.tool $(call quote,$(LAB3_REPORT))

test-unit:
	@$(call quote,$(DOTNET)) test tests/HospitalInformationSystem.UnitTests -m:1 /p:UseSharedCompilation=false

test-integration:
	@$(call quote,$(DOTNET)) test tests/HospitalInformationSystem.IntegrationTests -m:1 /p:UseSharedCompilation=false

test-scripts:
	@$(call quote,$(PYTHON)) -m unittest discover -s scripts/tests
