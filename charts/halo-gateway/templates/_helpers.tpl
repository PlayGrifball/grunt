{{- define "halo-gateway.fullname" -}}
{{- if contains .Chart.Name .Release.Name -}}
{{- .Release.Name | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- printf "%s-%s" .Release.Name .Chart.Name | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- end -}}

{{- define "halo-gateway.selectorLabels" -}}
app.kubernetes.io/name: {{ .Chart.Name }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end -}}

{{- define "halo-gateway.labels" -}}
{{ include "halo-gateway.selectorLabels" . }}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" }}
app.kubernetes.io/version: {{ .Values.image.tag | default .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end -}}

{{- define "halo-gateway.image" -}}
{{- $i := .Values.image -}}
{{- if $i.digest -}}
{{ $i.repository }}@{{ $i.digest }}
{{- else -}}
{{ $i.repository }}:{{ $i.tag | default .Chart.AppVersion }}
{{- end -}}
{{- end -}}

{{- define "halo-gateway.claimName" -}}
{{- .Values.persistence.existingClaim | default (printf "%s-data" (include "halo-gateway.fullname" .)) -}}
{{- end -}}

{{/* Nested map -> Section__Key env vars; lists become __0, __1; null values are skipped. */}}
{{- define "halo-gateway.flatten" -}}
{{- $prefix := .prefix -}}
{{- range $k, $v := .value }}
{{- $name := ternary $k (printf "%s__%s" $prefix $k) (eq $prefix "") }}
{{- if kindIs "map" $v }}
{{- include "halo-gateway.flatten" (dict "prefix" $name "value" $v) }}
{{- else if kindIs "slice" $v }}
{{- range $i, $item := $v }}
{{- if kindIs "map" $item }}
{{- include "halo-gateway.flatten" (dict "prefix" (printf "%s__%d" $name $i) "value" $item) }}
{{- else }}
- name: {{ printf "%s__%d" $name $i }}
  value: {{ $item | toString | quote }}
{{- end }}
{{- end }}
{{- else if not (kindIs "invalid" $v) }}
- name: {{ $name }}
  value: {{ $v | toString | quote }}
{{- end }}
{{- end }}
{{- end -}}

{{/*
PromQL label matchers selecting the gateway's pushed series, for its alerts and dashboard: prometheusRule.selector, else
k8s_namespace_name="<release namespace>", job=~"(.+/)?<telemetry.serviceName or release name>" (the labels an OpenTelemetry
Collector promotes from the resource attributes the chart sets). The service name's regex metacharacters are quoted as
character classes (grunt[.]gateway), not with backslashes: Grafana doubles every backslash in a variable's value when it
fills in the dashboard's $sel, and grunt\\.gateway then matches nothing.
*/}}
{{- define "halo-gateway.metricsSelector" -}}
{{- $svc := .Values.telemetry.serviceName | default .Release.Name | toString -}}
{{- if .Values.prometheusRule.selector -}}
{{- .Values.prometheusRule.selector -}}
{{- else -}}
{{- if regexMatch "[\\\\^]" $svc }}{{ fail (printf "telemetry.serviceName %s: no ^ or \\ in it, which the alerts' and dashboard's job=~ matcher can only quote with a backslash (Grafana doubles those); or set prometheusRule.selector" $svc) }}{{ end -}}
{{- printf "k8s_namespace_name=%q, job=~%q" .Release.Namespace (printf "(.+/)?%s" (regexReplaceAll "[.+*?()|{}\\[$]" $svc "[${0}]")) -}}
{{- end -}}
{{- end }}

{{/*
The Grafana dashboard's uid: grafanaDashboard.uid, else <fullname>-<release namespace>, which Grafana takes up to 40
characters of; a longer one is cut to 31 and ends in a hash of the whole.
*/}}
{{- define "halo-gateway.dashboardUid" -}}
{{- $uid := .Values.grafanaDashboard.uid | default (printf "%s-%s" (include "halo-gateway.fullname" .) .Release.Namespace) -}}
{{- if gt (len $uid) 40 -}}
{{- printf "%s-%s" ($uid | trunc 31 | trimSuffix "-") (sha256sum $uid | trunc 8) -}}
{{- else -}}
{{- $uid -}}
{{- end -}}
{{- end }}
