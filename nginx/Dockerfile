FROM nginx:alpine

# 主框架設定
COPY nginx.conf /etc/nginx/nginx.conf

# 功能性 conf 片段 (client.conf 含 server 主體，api.conf / dev.conf 含 location 片段)
COPY conf.d/ /etc/nginx/conf.d/

EXPOSE 80

CMD ["nginx", "-g", "daemon off;"]
