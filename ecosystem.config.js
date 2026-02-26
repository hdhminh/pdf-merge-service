module.exports = {
  apps: [
    {
      name: "pdf-stamp-service",
      script: "./index.js",
      cwd: "/home/huynhminh/pdf-merge-service",
      instances: 1,
      exec_mode: "fork",
      autorestart: true,
      watch: false,
      max_memory_restart: "500M",
      env: {
        NODE_ENV: "production",
        PORT: 3000,
      },
    },
  ],
};
