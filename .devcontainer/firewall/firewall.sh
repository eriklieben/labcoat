#!/bin/bash
# Default-deny egress for the dev container. Runs as a sidecar in the dev container's network
# namespace, so the rules bind the dev container too, and the dev container (no capabilities, no
# sudo) cannot undo them.
#
#  * 443/tcp only, and only to addresses in the nft set `allowed4`.
#  * The set is filled by dnsmasq at the moment a client resolves an allowed name, so there is no
#    window between "name resolved" and "address allowed", and CDNs that hand out a different
#    address per lookup just work.
#  * The dev container's resolv.conf points at 127.0.0.1 (this dnsmasq). Names that are not on the
#    list get no answer, and only dnsmasq's own user may send DNS upstream, so DNS cannot carry
#    data to an arbitrary server either.
set -euo pipefail

DOMAINS=/etc/firewall/allowed-domains.txt
DOMAINS_LOCAL=/etc/firewall/allowed-domains.local.txt   # optional, gitignored, this machine only
READY=/run/firewall.ok
DNSUSER=nobody

UPSTREAM=$(awk '/^nameserver/ && $2 ~ /^[0-9.]+$/ {print $2; exit}' /etc/resolv.conf)
[ -n "$UPSTREAM" ] || { echo "no IPv4 nameserver in resolv.conf" >&2; exit 1; }
DNSUID=$(id -u "$DNSUSER")

hosts() { cat "$DOMAINS" "$DOMAINS_LOCAL" 2>/dev/null | sed -e 's/#.*//' -e 's/[[:space:]]//g' | grep -v '^$' | sort -u || true; }

nft delete table inet egress 2>/dev/null || true
nft -f - <<RULES
table inet egress {
  set allowed4 {
    type ipv4_addr
  }
  chain output {
    type filter hook output priority 0; policy drop;
    oif "lo" accept
    ct state established,related accept
    meta skuid $DNSUID ip daddr $UPSTREAM udp dport 53 accept
    meta skuid $DNSUID ip daddr $UPSTREAM tcp dport 53 accept
    ip daddr @allowed4 tcp dport 443 accept
    reject with icmpx type admin-prohibited
  }
  chain input {
    type filter hook input priority 0; policy drop;
    iif "lo" accept
    ct state established,related accept
  }
  chain forward {
    type filter hook forward priority 0; policy drop;
  }
}
RULES

CONF=/run/dnsmasq.conf
{
  echo "no-resolv"; echo "no-hosts"; echo "no-poll"
  echo "listen-address=127.0.0.1"; echo "bind-interfaces"; echo "port=53"
  echo "user=$DNSUSER"; echo "cache-size=0"; echo "filter-AAAA"; echo "log-facility=-"
  while read -r h; do
    echo "server=/$h/$UPSTREAM"
    echo "nftset=/$h/4#inet#egress#allowed4"
  done < <(hosts)
} > "$CONF"
dnsmasq --keep-in-foreground --conf-file="$CONF" &
DNSPID=$!
for _ in $(seq 1 20); do dig +short +time=1 +tries=1 @127.0.0.1 "$(hosts | head -1)" >/dev/null 2>&1 && break; sleep 0.5; done

# Prove it before reporting ready. Probes run as root here, so they cannot use the system resolver
# (only dnsmasq may send DNS out): ask dnsmasq, then connect to the answer.
probe() { local ip; ip=$(dig +short +time=2 +tries=1 @127.0.0.1 "$1" 2>/dev/null | grep -E '^[0-9.]+$' | head -1); [ -n "$ip" ] && curl -sS --connect-timeout 6 -o /dev/null --resolve "$1:443:$ip" "https://$1"; }
first=$(hosts | head -1)
probe "$first" || { echo "FAIL: allowed host $first not reachable" >&2; exit 1; }
if probe example.com; then echo "FAIL: example.com is reachable" >&2; exit 1; fi
if curl -sS --connect-timeout 4 -o /dev/null --resolve example.com:443:93.184.215.14 https://example.com 2>/dev/null; then
  echo "FAIL: direct connection to an unlisted address works" >&2; exit 1
fi
echo "egress locked: $(hosts | wc -l) hosts allowed, DNS only via dnsmasq, upstream $UPSTREAM"
touch "$READY"
wait "$DNSPID"
