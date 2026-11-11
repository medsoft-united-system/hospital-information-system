.DEFAULT_GOAL := help

PYTHON ?= python3
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

quote = '$(subst ','"'"',$(1))'
build_flag = $(if $(filter 1,$(NO_BUILD)),--no-build,)
trust_flag = $(if $(filter 1,$(TRUST_CA)),--trust-ca,)
lis_source_flag = $(if $(strip $(LIS_SOURCE)),--lis-source $(call quote,$(LIS_SOURCE)),)

.PHONY: help run demo db db-stop build test lab3-prepare lab3-run lab3-smoke

help:
	@printf '%s\n' \
	  'make run       — запустить PostgreSQL, API и Web с рабочими данными' \
	  'make demo      — одноразовый стенд для настройки администратора' \
	  'make db        — запустить только PostgreSQL и дождаться готовности' \
	  'make db-stop   — остановить PostgreSQL с сохранением данных' \
	  'make build     — собрать solution' \
	  'make test      — выполнить C#-тесты и проверки Python-скриптов' \
	  'make lab3-prepare — подготовить TLS his-api с сохранением рабочих ключей' \
	  'make lab3-run  — рабочая HIS в отдельной сети для подключения Go LIS' \
	  'make lab3-smoke — изолированная HTTPS-проверка лабораторной 3' \
	  '' \
	  'Параметры: ISU_ID=408944, SECURITY_DIR=/path/to/security, NO_BUILD=1.' \
	  'make demo TRUST_CA=0 — не добавлять CA в связку ключей повторно.' \
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
