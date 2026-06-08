import { Environment } from '@abp/ng.core';

const baseUrl = 'http://localhost:4200';

const oAuthConfig = {
  issuer: 'https://localhost:44348/',
  redirectUri: baseUrl,
  clientId: 'Paperbase_App',
  responseType: 'code',
  scope: 'offline_access Paperbase',
  requireHttps: true,
};

export const environment = {
  production: true,
  // 运行时从 getEnvConfig（nginx/IIS 重写到 dynamic-env.json）拉取部署期配置，
  // deepmerge 覆盖到下面的静态默认值之上——同一构建产物可换 JSON 部署到不同环境。
  // 用相对路径（无前导斜杠）：ABP 经 HttpClient → XHR 按 <base href> 解析，因此自动
  // 跟随构建时 --base-href（根部署→/getEnvConfig，子路径部署→<base>/getEnvConfig），无需写死。
  // 拉取失败（如未经反向代理直接托管）时 ABP 静默回退到静态默认值。
  remoteEnv: {
    url: 'getEnvConfig',
    mergeStrategy: 'deepmerge',
  },
  application: {
    baseUrl,
    name: 'Paperbase',
  },
  oAuthConfig,
  apis: {
    default: {
      url: 'https://localhost:44348',
      rootNamespace: 'Dignite.Paperbase',
    },
    AbpAccountPublic: {
      url: oAuthConfig.issuer,
      rootNamespace: 'AbpAccountPublic',
    },
  },
} as Environment;
