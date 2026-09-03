FROM oven/bun:1.3 AS build
WORKDIR /app
COPY src/frontend/package.json src/frontend/bun.lock ./
RUN bun install --frozen-lockfile
COPY src/frontend/ .
RUN bun run build
FROM nginx:1.29-alpine
COPY src/frontend/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 80
