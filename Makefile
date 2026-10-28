.DEFAULT_GOAL := help

PYTHON ?= python3
DOTNET ?= dotnet
DOCKER ?= docker
ISU_ID ?= 408944
SECURITY_DIR ?= $(HOME)/.local/share/MedSoft/development-security/$(ISU_ID)
TRUST_CA ?= 1
NO_BUILD ?= 0

quote = '$(subst ','"'"',$(1))'
build_flag = $(if $(filter 1,$(NO_BUILD)),--no-build,)
trust_flag = $(if $(filter 1,$(TRUST_CA)),--trust-ca,)

.PHONY: help run demo db db-stop build test

help:
	@printf '%s\n' \
	  'make run       — запустить PostgreSQL, API и Web с рабочими данными' \
	  'make demo      — одноразовый стенд для настройки администратора' \
	  'make db        — запустить только PostgreSQL и дождаться готовности' \
	  'make db-stop   — остановить PostgreSQL с сохранением данных' \
	  'make build     — собрать solution' \
	  'make test      — выполнить C#-тесты и проверки Python-скриптов' \
	  '' \
	  'Параметры: ISU_ID=408944, SECURITY_DIR=/path/to/security, NO_BUILD=1.' \
	  'make demo TRUST_CA=0 — не добавлять CA в связку ключей повторно.' \
	  'Docker Desktop должен работать. Остановка приложения: Ctrl+C.'

run:
	@$(call quote,$(PYTHON)) scripts/run-application.py --start-database \
	  --security-directory $(call quote,$(SECURITY_DIR)) --docker $(call quote,$(DOCKER)) $(build_flag)

demo:
	@$(call quote,$(PYTHON)) scripts/run-setup-demo.py --isu-id $(call quote,$(ISU_ID)) \
	  --docker $(call quote,$(DOCKER)) $(trust_flag) $(build_flag)

db:
	@$(call quote,$(DOCKER)) compose --file docker-compose.yml up -d --wait postgres

db-stop:
	@$(call quote,$(DOCKER)) compose --file docker-compose.yml stop postgres

build:
	@$(call quote,$(DOTNET)) build HospitalInformationSystem.slnx

test:
	@$(call quote,$(DOTNET)) test HospitalInformationSystem.slnx
	@$(call quote,$(PYTHON)) -m unittest discover -s scripts/tests
