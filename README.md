# ai-news-pipeline

RSS/AI 新闻摘要、X 草稿及 Telegram 人工审核。

RSS news pipeline with AI summaries, X post drafts and Telegram review using Azure Functions.

## Category / 分类

AI & Automation

## Technology / 技术栈

C#, .NET, Azure Functions

## Install & run / 安装运行

```sh
dotnet restore
dotnet build
# 从 README.original.md 创建本地设置；安装 Azure Functions Core Tools 后运行 func start。
```

原项目的完整说明保存在 [README.original.md](README.original.md)，以其中的详细依赖、功能和操作说明为准。

## Structure / 目录

```text
.github/
.gitignore
.vscode/
AutoTweetRss.csproj
Functions/
Program.cs
README.original.md
README_DEPLOY_LOCAL.md
Services/
assets/
auto-tweet-rss.sln
host.json
restart-functions-local.sh
start-azurite.sh
start-functions.sh
test-vscode-notes/
```

## Status / 当前状态

本仓库是本地项目的整理快照。仅完成上传相关的静态检查和敏感信息扫描；除验证报告明确列出的项目外，未重新运行完整应用、交易、发布或外部服务流程。功能与已知限制见原项目文档。

## Roadmap

统一新闻源分类；测试去重及审核流程；补充部署验收。

## Configuration / 配置与数据

凭据、Cookie、本机状态、依赖目录、构建产物、模型权重与大型数据没有纳入发布。复制后需自行安装依赖，并通过本地环境变量或未跟踪的配置文件提供访问凭据。已排除全部 `.env*` 文件；需要的变量名见代码及原文档。

## Topics

c, net, azure-functions, automation

## Deployment trigger / 部署触发

Azure 部署工作流仅允许在 GitHub Actions 中手动触发；普通 push 不会部署线上 Function App。
