FROM mcr.microsoft.com/dotnet/sdk:11.0

# Install common dev tools, Node.js 24, and Docker CLI
RUN apt-get update && export DEBIAN_FRONTEND=noninteractive \
    && apt-get install -y --no-install-recommends \
        git curl wget sudo ca-certificates gnupg2 \
        procps lsb-release openssh-client socat \
    # Add NodeSource repo for Node.js 24
    && curl -fsSL https://deb.nodesource.com/setup_26.x | bash - \
    && apt-get install -y --no-install-recommends nodejs \
    # Add Docker CLI (detect distro automatically)
    && install -m 0755 -d /etc/apt/keyrings \
    && . /etc/os-release \
    && if [ "$ID" = "ubuntu" ]; then \
         curl -fsSL https://download.docker.com/linux/ubuntu/gpg | gpg --dearmor -o /etc/apt/keyrings/docker.gpg; \
         echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] https://download.docker.com/linux/ubuntu $VERSION_CODENAME stable" > /etc/apt/sources.list.d/docker.list; \
       else \
         curl -fsSL https://download.docker.com/linux/debian/gpg | gpg --dearmor -o /etc/apt/keyrings/docker.gpg; \
         echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] https://download.docker.com/linux/debian $VERSION_CODENAME stable" > /etc/apt/sources.list.d/docker.list; \
       fi \
    && chmod a+r /etc/apt/keyrings/docker.gpg \
    # Add GitHub CLI repo
    && curl -fsSL https://cli.github.com/packages/githubcli-archive-keyring.gpg -o /etc/apt/keyrings/githubcli-archive-keyring.gpg \
    && chmod a+r /etc/apt/keyrings/githubcli-archive-keyring.gpg \
    && echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/githubcli-archive-keyring.gpg] https://cli.github.com/packages stable main" > /etc/apt/sources.list.d/github-cli.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends docker-ce-cli docker-compose-plugin gh \
    # Cleanup
    && apt-get autoremove -y && apt-get clean -y \
    && rm -rf /var/lib/apt/lists/*

# Create non-root user (handle pre-existing GID/UID gracefully)
ARG USERNAME=vscode
ARG USER_UID=1000
ARG USER_GID=$USER_UID
RUN groupadd --gid $USER_GID $USERNAME 2>/dev/null || true \
    && useradd --uid $USER_UID --gid $USER_GID -m $USERNAME -s /bin/bash 2>/dev/null \
       || usermod -l $USERNAME -d /home/$USERNAME -m $(getent passwd $USER_UID | cut -d: -f1) 2>/dev/null || true \
    && echo "$USERNAME ALL=(ALL) NOPASSWD:ALL" >> /etc/sudoers.d/$USERNAME \
    && chmod 0440 /etc/sudoers.d/$USERNAME

# Add user to docker group (will be created if socket is mounted)
RUN groupadd -f docker && usermod -aG docker $USERNAME

# Claude Code CLI (installed via npm rather than the devcontainer feature,
# which is unreliable under envbuilder/kaniko)
RUN npm install -g @anthropic-ai/claude-code

# Literal user name: kaniko/envbuilder stores USER with unexpanded ARG in the
# image config, which breaks envbuilder's user lookup ("unknown user $USERNAME").
USER vscode
