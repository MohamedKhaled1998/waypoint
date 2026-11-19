FROM mysql:8.0
USER root
COPY *.sh /backup/
RUN chmod +x /backup/*.sh
ENTRYPOINT []
CMD ["/backup/schedule.sh"]
