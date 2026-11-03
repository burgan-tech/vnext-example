# python-task-lab domain image: the vNext base image plus this lab's handlers and requirements —
# the same shape a domain owner builds (vnext docs/runtime/python-task.md § Domain image).
ARG VNEXT_PYTHON_RUNTIME=vnext-python-runtime:local
FROM ${VNEXT_PYTHON_RUNTIME}

USER root
COPY requirements.txt /tmp/requirements.txt
RUN pip install --no-cache-dir -r /tmp/requirements.txt && rm /tmp/requirements.txt
COPY handlers /opt/vnext-python/app/handlers
USER 65532:65532
