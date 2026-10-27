{{- define "fms.name" -}}{{ .Chart.Name }}{{- end -}}

{{- define "fms.fullname" -}}
{{- if contains .Chart.Name .Release.Name -}}{{ .Release.Name | trunc 50 | trimSuffix "-" }}{{- else -}}{{ printf "%s-%s" .Release.Name .Chart.Name | trunc 50 | trimSuffix "-" }}{{- end -}}
{{- end -}}

{{- define "fms.labels" -}}
app.kubernetes.io/name: {{ include "fms.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version }}
{{- end -}}

{{- define "fms.selector" -}}
app.kubernetes.io/name: {{ include "fms.name" .root }}
app.kubernetes.io/instance: {{ .root.Release.Name }}
app.kubernetes.io/component: {{ .component }}
{{- end -}}

{{- define "fms.image" -}}
{{ printf "%s/fms-%s:%s" .root.Values.image.registry .name (default .root.Chart.AppVersion .root.Values.image.tag) }}
{{- end -}}

{{- define "fms.serviceAccount" -}}
{{- if .Values.serviceAccount.create -}}{{ include "fms.fullname" . }}{{- else -}}default{{- end -}}
{{- end -}}

{{/* Every pod runs as a non-root user with nothing it does not need. The root file system is read-only; /tmp is a small scratch volume. */}}
{{- define "fms.podSecurity" -}}
runAsNonRoot: true
seccompProfile:
  type: RuntimeDefault
{{- end -}}

{{- define "fms.containerSecurity" -}}
allowPrivilegeEscalation: false
readOnlyRootFilesystem: true
capabilities:
  drop: ["ALL"]
{{- end -}}

{{/* Settings shared by the API, the worker and the migrator. */}}
{{- define "fms.commonEnv" -}}
- name: Deployment__Mode
  value: {{ .Values.deployment.mode | quote }}
{{- if eq .Values.deployment.mode "OnPrem" }}
- name: Deployment__SingleTenantId
  value: {{ required "deployment.tenantId is required for OnPrem" .Values.deployment.tenantId | quote }}
- name: Deployment__LocalStoragePath
  value: /data/files
{{- end }}
- name: Storage__Provider
  value: {{ .Values.storage.provider | quote }}
{{- with .Values.storage.extraEnv }}
{{ toYaml . }}
{{- end }}
{{- end -}}
