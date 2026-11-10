package main

import (
	"bufio"
	"context"
	"crypto/tls"
	"crypto/x509"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"net"
	"net/url"
	"os"
	"strings"
	"time"

	"laboratory-information-system/lis/internal/infrastructure/fhir"
	"laboratory-information-system/lis/internal/infrastructure/hisauth"
	"laboratory-information-system/lis/internal/integration"
)

type config struct {
	Origin     string `json:"origin"`
	CAFile     string `json:"caFile"`
	KeyFile    string `json:"keyFile"`
	RequestID  string `json:"requestId"`
	Result     string `json:"result"`
	ReportFile string `json:"reportFile"`
}
type bearer struct{ value string }

func (b *bearer) Access(context.Context, integration.Snapshot) (string, error) { return b.value, nil }

func event(stage string) {
	_ = json.NewEncoder(os.Stdout).Encode(map[string]string{"stage": stage, "status": "passed"})
}
func wait(stage string, input *bufio.Scanner) error {
	_ = json.NewEncoder(os.Stdout).Encode(map[string]string{"stage": stage, "status": "waiting"})
	if !input.Scan() || strings.TrimSpace(input.Text()) != "continue" {
		return errors.New("expected continue command")
	}
	return nil
}
func secret(path string) (string, error) {
	info, err := os.Lstat(path)
	if err != nil {
		return "", err
	}
	if !info.Mode().IsRegular() || info.Mode().Perm()&0077 != 0 {
		return "", errors.New("key file must be regular with mode 0600")
	}
	data, err := os.ReadFile(path)
	if err != nil {
		return "", err
	}
	value := strings.TrimRight(string(data), "\r\n")
	if value == "" || strings.ContainsAny(value, "\r\n\x00") {
		return "", errors.New("invalid key file")
	}
	return value, nil
}
func rejected(err error) bool {
	var fhirError *fhir.Error
	return errors.As(err, &fhirError) && fhirError.HTTPStatus == 401 || err != nil && strings.Contains(err.Error(), "(401)")
}
func run(cfg config, interactive, connectOnly bool) error {
	ca, err := os.ReadFile(cfg.CAFile)
	if err != nil {
		return err
	}
	origin, err := integration.ParseOrigin(cfg.Origin)
	if err != nil {
		return err
	}
	address, err := url.Parse(origin.String())
	if err != nil {
		return err
	}
	roots, err := x509.SystemCertPool()
	if err != nil {
		roots = x509.NewCertPool()
	}
	if !roots.AppendCertsFromPEM(ca) {
		return errors.New("invalid CA")
	}
	host := address.Hostname()
	port := address.Port()
	if port == "" {
		port = "443"
	}
	connection, err := tls.DialWithDialer(&net.Dialer{Timeout: 10 * time.Second}, "tcp", net.JoinHostPort(host, port), &tls.Config{RootCAs: roots, ServerName: host, MinVersion: tls.VersionTLS13, MaxVersion: tls.VersionTLS13})
	if err != nil {
		return err
	}
	connection.Close()
	event("verified_tls13_ca_hostname")
	auth, err := hisauth.New(ca)
	if err != nil {
		return err
	}
	key, err := secret(cfg.KeyFile)
	if err != nil {
		return err
	}
	ctx := context.Background()
	pair, err := auth.Token(ctx, origin, key)
	if err != nil {
		return err
	}
	if err = auth.Probe(ctx, origin, pair.Access); err != nil {
		return err
	}
	event("go_token_testConnection_metadata")
	provider := &bearer{value: pair.Access}
	medical, err := fhir.New(ca, provider)
	if err != nil {
		return err
	}
	snapshot := integration.Snapshot{EndpointID: "55555555-5555-4555-8555-555555555555", Origin: origin, Version: 1}
	if _, err = medical.GetMetadata(ctx, snapshot); err != nil {
		return err
	}
	if connectOnly {
		event("connection_smoke_complete")
		return nil
	}
	order, err := medical.GetServiceRequest(ctx, snapshot, cfg.RequestID)
	if err != nil {
		return err
	}
	if order.Value.Status != "active" {
		return errors.New("smoke requires an unused active order")
	}
	patientID := strings.TrimPrefix(order.Value.Subject.Reference, "Patient/")
	practitionerID := strings.TrimPrefix(order.Value.Requester.Reference, "Practitioner/")
	if _, err = medical.GetPatient(ctx, snapshot, patientID); err != nil {
		return err
	}
	if _, err = medical.GetPractitioner(ctx, snapshot, practitionerID); err != nil {
		return err
	}
	orders, err := medical.SearchServiceRequests(ctx, snapshot, "active", 1)
	if err != nil {
		return err
	}
	found := false
	for _, candidate := range orders {
		if candidate.Value.ID == cfg.RequestID {
			found = true
		}
	}
	if !found {
		return errors.New("order missing from complete paged search")
	}
	event("go_four_resources_and_pagination")
	result := cfg.Result
	if result == "" {
		result = "5.6"
	}
	input, err := fhir.NewObservation(order.Value, result, time.Now())
	if err != nil {
		return err
	}
	wrong := input
	wrong.Subject.Reference = "Patient/99999999-9999-4999-8999-999999999999"
	if _, err = medical.CreateObservation(ctx, snapshot, order.Value, wrong); err == nil {
		return errors.New("Go client accepted mismatched patient")
	}
	event("go_rejected_wrong_patient_before_post")
	created, err := medical.CreateObservation(ctx, snapshot, order.Value, input)
	if err != nil {
		return err
	}
	event("go_observation_201")
	read, err := medical.GetObservation(ctx, snapshot, created.Value.ID)
	if err != nil {
		return err
	}
	if read.Value.ID != created.Value.ID {
		return errors.New("result GET mismatch")
	}
	completed, err := medical.GetServiceRequest(ctx, snapshot, cfg.RequestID)
	if err != nil {
		return err
	}
	if completed.Value.Status != "completed" {
		return errors.New("order not completed")
	}
	foundResult, err := medical.FindObservationByServiceRequest(ctx, snapshot, completed.Value)
	if err != nil {
		return err
	}
	if foundResult == nil || foundResult.Value.ID != created.Value.ID {
		return errors.New("result search mismatch")
	}
	_, err = medical.CreateObservation(ctx, snapshot, order.Value, input)
	var duplicate *fhir.Error
	if !errors.As(err, &duplicate) || duplicate.HTTPStatus != 409 || duplicate.Code != "result_already_exists" {
		return errors.New("duplicate POST did not return expected 409")
	}
	event("go_completed_readback_and_duplicate_409")
	previous := pair
	pair, err = auth.Refresh(ctx, origin, previous.Refresh)
	if err != nil {
		return err
	}
	if !pair.RefreshExpires.Equal(previous.RefreshExpires) {
		return errors.New("refresh extended family expiry")
	}
	if err = auth.Probe(ctx, origin, previous.Access); err != nil {
		return errors.New("ordinary refresh invalidated old access")
	}
	if err = auth.Probe(ctx, origin, pair.Access); err != nil {
		return err
	}
	event("go_refresh_keeps_old_access_and_family_deadline")
	_, err = auth.Refresh(ctx, origin, previous.Refresh)
	if !rejected(err) {
		return errors.New("refresh reuse not rejected")
	}
	if err = auth.Probe(ctx, origin, pair.Access); !rejected(err) {
		return errors.New("reuse did not revoke replacement access")
	}
	provider.value = pair.Access
	_, err = medical.GetServiceRequest(ctx, snapshot, cfg.RequestID)
	if !rejected(err) {
		return errors.New("revoked bearer not rejected by FHIR")
	}
	event("go_reuse_revokes_family_and_fhir_access")
	if interactive {
		pair, err = auth.Token(ctx, origin, key)
		if err != nil {
			return err
		}
		input := bufio.NewScanner(os.Stdin)
		if err = wait("awaiting_key_rotation_and_key_file_update", input); err != nil {
			return err
		}
		if err = auth.Probe(ctx, origin, pair.Access); !rejected(err) {
			return errors.New("rotation left old access valid")
		}
		_, err = auth.Refresh(ctx, origin, pair.Refresh)
		if !rejected(err) {
			return errors.New("rotation left old refresh valid")
		}
		_, err = auth.Token(ctx, origin, key)
		if !rejected(err) {
			return errors.New("rotation left old key valid")
		}
		event("go_key_rotation_revokes_access_refresh_and_key")
		key, err = secret(cfg.KeyFile)
		if err != nil {
			return err
		}
		pair, err = auth.Token(ctx, origin, key)
		if err != nil {
			return err
		}
		if err = auth.Probe(ctx, origin, pair.Access); err != nil {
			return err
		}
		if err = wait("awaiting_account_deactivation", input); err != nil {
			return err
		}
		if err = auth.Probe(ctx, origin, pair.Access); !rejected(err) {
			return errors.New("deactivation left access valid")
		}
		_, err = auth.Refresh(ctx, origin, pair.Refresh)
		if !rejected(err) {
			return errors.New("deactivation left refresh valid")
		}
		_, err = auth.Token(ctx, origin, key)
		if !rejected(err) {
			return errors.New("deactivation left key usable")
		}
		event("go_deactivation_revokes_access_refresh_and_login")
		if err = wait("awaiting_account_reactivation", input); err != nil {
			return err
		}
		if err = auth.Probe(ctx, origin, pair.Access); !rejected(err) {
			return errors.New("reactivation restored old access")
		}
		_, err = auth.Refresh(ctx, origin, pair.Refresh)
		if !rejected(err) {
			return errors.New("reactivation restored old refresh")
		}
		pair, err = auth.Token(ctx, origin, key)
		if err != nil {
			return err
		}
		if err = auth.Probe(ctx, origin, pair.Access); err != nil {
			return err
		}
		event("go_reactivation_requires_new_session")
	}
	if cfg.ReportFile != "" {
		file, err := os.OpenFile(cfg.ReportFile, os.O_WRONLY|os.O_CREATE|os.O_EXCL, 0600)
		if err != nil {
			return err
		}
		err = json.NewEncoder(file).Encode(map[string]any{
			"requestId": cfg.RequestID, "observationId": created.Value.ID,
			"valueString": *created.Value.ValueString, "issued": created.Value.Issued,
			"status": "completed", "interactiveRevocationChecked": interactive,
		})
		file.Close()
		if err != nil {
			return err
		}
	}
	event("go_wire_smoke_complete")
	return nil
}
func main() {
	path := flag.String("config", "", "private smoke config path")
	interactive := flag.Bool("interactive", false, "wait for operator rotation/deactivation/reactivation")
	connectOnly := flag.Bool("connect-only", false, "only TLS and actual LIS technical clients")
	flag.Parse()
	data, err := os.ReadFile(*path)
	var cfg config
	if err == nil {
		err = json.Unmarshal(data, &cfg)
	}
	if err == nil {
		err = run(cfg, *interactive, *connectOnly)
	}
	if err != nil {
		_ = json.NewEncoder(os.Stdout).Encode(map[string]string{"status": "failed", "errorType": fmt.Sprintf("%T", err), "message": err.Error()})
		os.Exit(1)
	}
}
