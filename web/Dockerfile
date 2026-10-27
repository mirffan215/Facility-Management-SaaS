FROM node:22-alpine AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci
COPY . .
RUN npm run build

FROM nginxinc/nginx-unprivileged:alpine
# Runs as the unprivileged nginx user on port 8080 (no root, no privileged port), so the container can run with a read-only root file system.
# The official image renders /etc/nginx/templates/*.template with the container's environment on start.
COPY nginx.conf.template /etc/nginx/templates/default.conf.template
COPY --from=build /app/dist /usr/share/nginx/html
ENV API_UPSTREAM=http://api:8080 \
    CSP_CONNECT_SRC="https:" \
    NGINX_ENVSUBST_FILTER="API_UPSTREAM|CSP_CONNECT_SRC"
