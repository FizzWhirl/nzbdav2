# syntax=docker/dockerfile:1.4

# Build the project
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build-env
WORKDIR /nzb-webdav
ARG TARGETARCH
COPY ./ ./
RUN dotnet restore
RUN case "${TARGETARCH:-amd64}" in \
			amd64) DOTNET_ARCH=x64 ;; \
			arm64) DOTNET_ARCH=arm64 ;; \
			*) echo "Unsupported TARGETARCH: $TARGETARCH" && exit 1 ;; \
		esac && \
		dotnet publish -c Release -r linux-musl-$DOTNET_ARCH -o ./publish

# Build the production image
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
EXPOSE 8080
WORKDIR /nzb-webdav
RUN mkdir /config
RUN apk add --no-cache libc6-compat shadow su-exec && rm -rf /var/cache/apk/*
COPY --from=build-env /nzb-webdav/publish ./
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh
CMD ["/entrypoint.sh"]