# syntax=docker/dockerfile:1.7

# =============================================================================================
# Build
# =============================================================================================
FROM node:22-alpine AS build

WORKDIR /app

# `npm ci` from the lockfile: reproducible, and cached independently of source changes.
COPY package.json package-lock.json ./
RUN npm ci

COPY . .
RUN npm run build

# =============================================================================================
# Runtime
# =============================================================================================
# The output is a static bundle, so nothing from the build stage — Node, the module tree, the
# toolchain — belongs in the final image. This lands at roughly 50 MB, nearly all of it nginx.
FROM nginx:1.27-alpine AS runtime

COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html

EXPOSE 8080

HEALTHCHECK --interval=15s --timeout=3s --retries=3 \
    CMD wget --quiet --spider http://localhost:8080/ || exit 1
