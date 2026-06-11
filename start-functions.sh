#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")" && pwd)"
RUN_DIR="$ROOT_DIR/bin/Debug/net10.0"

export PATH="$ROOT_DIR/.tools/node_modules/azure-functions-core-tools/bin:$ROOT_DIR/.tools/node_modules/.bin:$ROOT_DIR/.dotnet:$PATH"
export DOTNET_CLI_HOME="$ROOT_DIR/.dotnet-home"
export NUGET_PACKAGES="$ROOT_DIR/.nuget/packages"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

cd "$RUN_DIR"

export OPENAI_API_KEY="$(node -e "const fs=require('fs'); const v=JSON.parse(fs.readFileSync('local.settings.json','utf8')).Values; process.stdout.write(v.OPENAI_API_KEY || '')")"
export OPENAI_BASE_URL="$(node -e "const fs=require('fs'); const v=JSON.parse(fs.readFileSync('local.settings.json','utf8')).Values; process.stdout.write(v.OPENAI_BASE_URL || 'https://api.openai.com/v1')")"
export OPENAI_MODEL="$(node -e "const fs=require('fs'); const v=JSON.parse(fs.readFileSync('local.settings.json','utf8')).Values; process.stdout.write(v.OPENAI_MODEL || 'gpt-4o-mini')")"
export DRY_RUN="$(node -e "const fs=require('fs'); const v=JSON.parse(fs.readFileSync('local.settings.json','utf8')).Values; process.stdout.write(v.DRY_RUN || 'true')")"
export X_API_ENABLED="$(node -e "const fs=require('fs'); const v=JSON.parse(fs.readFileSync('local.settings.json','utf8')).Values; process.stdout.write(v.X_API_ENABLED || 'false')")"
export MANUAL_X_POST_MODE="$(node -e "const fs=require('fs'); const v=JSON.parse(fs.readFileSync('local.settings.json','utf8')).Values; process.stdout.write(v.MANUAL_X_POST_MODE || 'true')")"
export GENERATE_X_INTENT_LINK="$(node -e "const fs=require('fs'); const v=JSON.parse(fs.readFileSync('local.settings.json','utf8')).Values; process.stdout.write(v.GENERATE_X_INTENT_LINK || 'true')")"
export USE_OPENAI_FOR_SCORING="$(node -e "const fs=require('fs'); const v=JSON.parse(fs.readFileSync('local.settings.json','utf8')).Values; process.stdout.write(v.USE_OPENAI_FOR_SCORING || 'false')")"
export USE_OPENAI_FOR_TWEET="$(node -e "const fs=require('fs'); const v=JSON.parse(fs.readFileSync('local.settings.json','utf8')).Values; process.stdout.write(v.USE_OPENAI_FOR_TWEET || 'true')")"
export LOCAL_SCORE_MIN="$(node -e "const fs=require('fs'); const v=JSON.parse(fs.readFileSync('local.settings.json','utf8')).Values; process.stdout.write(v.LOCAL_SCORE_MIN || v.NEWS_LOCAL_SCORE_MIN || '70')")"
export OPENAI_MAX_CANDIDATES="$(node -e "const fs=require('fs'); const v=JSON.parse(fs.readFileSync('local.settings.json','utf8')).Values; process.stdout.write(v.OPENAI_MAX_CANDIDATES || v.NEWS_OPENAI_MAX_CANDIDATES || '3')")"
export TELEGRAM_ENABLED="$(node -e "const fs=require('fs'); const v=JSON.parse(fs.readFileSync('local.settings.json','utf8')).Values; process.stdout.write(v.TELEGRAM_ENABLED || 'false')")"
export TELEGRAM_REVIEW_MODE="$(node -e "const fs=require('fs'); const v=JSON.parse(fs.readFileSync('local.settings.json','utf8')).Values; process.stdout.write(v.TELEGRAM_REVIEW_MODE || 'true')")"
export TELEGRAM_BUTTON_BASE_URL="$(node -e "const fs=require('fs'); const v=JSON.parse(fs.readFileSync('local.settings.json','utf8')).Values; process.stdout.write(v.TELEGRAM_BUTTON_BASE_URL || 'http://127.0.0.1:7071/api')")"
export TELEGRAM_BOT_USERNAME="$(node -e "const fs=require('fs'); const v=JSON.parse(fs.readFileSync('local.settings.json','utf8')).Values; process.stdout.write(v.TELEGRAM_BOT_USERNAME || '')")"
export TELEGRAM_BOT_TOKEN="$(node -e "const fs=require('fs'); const v=JSON.parse(fs.readFileSync('local.settings.json','utf8')).Values; process.stdout.write(v.TELEGRAM_BOT_TOKEN || '')")"
export TELEGRAM_CHAT_ID="$(node -e "const fs=require('fs'); const v=JSON.parse(fs.readFileSync('local.settings.json','utf8')).Values; process.stdout.write(v.TELEGRAM_CHAT_ID || '')")"
export X_TWEET_LANGUAGE="$(node -e "const fs=require('fs'); const v=JSON.parse(fs.readFileSync('local.settings.json','utf8')).Values; process.stdout.write(v.X_TWEET_LANGUAGE || 'en')")"
export TELEGRAM_INCLUDE_CHINESE_BRIEF="$(node -e "const fs=require('fs'); const v=JSON.parse(fs.readFileSync('local.settings.json','utf8')).Values; process.stdout.write(v.TELEGRAM_INCLUDE_CHINESE_BRIEF || 'true')")"
export ENGLISH_TWEET_BODY_MAX_CHARS="$(node -e "const fs=require('fs'); const v=JSON.parse(fs.readFileSync('local.settings.json','utf8')).Values; process.stdout.write(v.ENGLISH_TWEET_BODY_MAX_CHARS || '240')")"
export CHINESE_BRIEF_MAX_CHARS="$(node -e "const fs=require('fs'); const v=JSON.parse(fs.readFileSync('local.settings.json','utf8')).Values; process.stdout.write(v.CHINESE_BRIEF_MAX_CHARS || '160')")"
export ENABLE_TWEET_IMAGE="$(node -e "const fs=require('fs'); const v=JSON.parse(fs.readFileSync('local.settings.json','utf8')).Values; process.stdout.write(v.ENABLE_TWEET_IMAGE || 'true')")"
export DEFAULT_TWEET_IMAGE_PATH="$(node -e "const fs=require('fs'); const v=JSON.parse(fs.readFileSync('local.settings.json','utf8')).Values; process.stdout.write(v.DEFAULT_TWEET_IMAGE_PATH || 'assets/default-news.png')")"
export MIN_SCORE="$(node -e "const fs=require('fs'); const v=JSON.parse(fs.readFileSync('local.settings.json','utf8')).Values; process.stdout.write(v.MIN_SCORE || v.NEWS_MIN_SCORE || '80')")"
export MIN_AI_IMPACT_SCORE="$(node -e "const fs=require('fs'); const v=JSON.parse(fs.readFileSync('local.settings.json','utf8')).Values; process.stdout.write(v.MIN_AI_IMPACT_SCORE || '7')")"
export FALLBACK_TOP_N="$(node -e "const fs=require('fs'); const v=JSON.parse(fs.readFileSync('local.settings.json','utf8')).Values; process.stdout.write(v.FALLBACK_TOP_N || v.NEWS_FALLBACK_TOP_N || '3')")"
export FALLBACK_MIN_SCORE="$(node -e "const fs=require('fs'); const v=JSON.parse(fs.readFileSync('local.settings.json','utf8')).Values; process.stdout.write(v.FALLBACK_MIN_SCORE || v.NEWS_FALLBACK_MIN_SCORE || '70')")"
export MAX_TWEETS_PER_RUN="$(node -e "const fs=require('fs'); const v=JSON.parse(fs.readFileSync('local.settings.json','utf8')).Values; process.stdout.write(v.MAX_TWEETS_PER_RUN || v.NEWS_MAX_POSTS_PER_RUN || '3')")"

func start --verbose --port "${PORT:-7071}"
