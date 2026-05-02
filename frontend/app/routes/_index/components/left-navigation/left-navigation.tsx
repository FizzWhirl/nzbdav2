import { Form, Link, useLocation, useNavigation } from "react-router";
import styles from "./left-navigation.module.css";
import { className } from "~/utils/styling";
import type React from "react";
import { LiveUsenetConnections } from "../live-usenet-connections/live-usenet-connections";

export type LeftNavigationProps = {
    version?: string,
    isFrontendAuthDisabled?: boolean,
    statsEnabled?: boolean,
}

export function LeftNavigation({ version, isFrontendAuthDisabled, statsEnabled }: LeftNavigationProps) {
    return (
        <div className={styles.container}>
            <div className={styles["nav-main"]}>
                <Item target="/">
                    <div className={styles["home-icon"]} />
                    <div className={styles.title}>Home</div>
                </Item>
                <Item target="/queue">
                    <div className={styles["queue-icon"]} />
                    <div className={styles.title}>Queue & History</div>
                </Item>
                <Item target="/explore">
                    <div className={styles["explore-icon"]} />
                    <div className={styles.title}>Dav Explore</div>
                </Item>
                <Item target="/health" keepMenuOpen>
                    <div className={styles["health-icon"]} />
                    <div className={styles.title}>Health</div>
                </Item>
                <SubNav parent="/health" items={[
                    ["/health?tab=health", "Health Queue"],
                    ["/health?tab=analysis", "Active Analyses"],
                    ["/health?tab=analysis-history", "Analysis History"],
                ]} />
                {statsEnabled && <>
                    <Item target="/stats" keepMenuOpen>
                        <div className={styles["stats-icon"]} />
                        <div className={styles.title}>Stats</div>
                    </Item>
                    <SubNav parent="/stats" items={[
                        ["/stats?tab=stats", "Statistics"],
                        ["/stats?tab=deleted", "Deleted Files"],
                        ["/stats?tab=missing", "Missing Articles"],
                        ["/stats?tab=mapped", "Mapped Files"],
                        ["/stats?tab=logs", "System Logs"],
                    ]} />
                </>}
                <Item target="/settings" keepMenuOpen>
                    <div className={styles["settings-icon"]} />
                    <div className={styles.title}>Settings</div>
                </Item>
                <SubNav parent="/settings" items={[
                    ["/settings?tab=general", "General"],
                    ["/settings?tab=usenet", "Usenet"],
                    ["/settings?tab=sabnzbd", "SABnzbd"],
                    ["/settings?tab=webdav", "WebDAV"],
                    ["/settings?tab=arrs", "Radarr/Sonarr"],
                    ["/settings?tab=repairs", "Repairs"],
                    ["/settings?tab=debug", "Debug Logs"],
                    ["/settings?tab=maintenance", "Maintenance"],
                ]} />
                <LiveUsenetConnections />

                <div className={styles.footer}>
                    <div className={styles["footer-item"]}>
                        <Link to="https://github.com/FizzWhirl/nzbdav2" className={styles["github-link"]}>
                            github
                        </Link>
                        <div className={styles["github-icon"]} />
                    </div>
                    <div className={styles["footer-item"]}>
                        <Link to="https://github.com/FizzWhirl/nzbdav2#changelog" className={styles["github-link"]}>
                            changelog
                        </Link>
                    </div>
                    <div className={styles["footer-item"]}>
                        version: {version || 'unknown'}
                    </div>
                    {!isFrontendAuthDisabled && <>
                        <hr />
                        <Form method="post" action="/logout">
                            <input name="confirm" value="true" type="hidden" />
                            <button className={styles.unstyled + ' ' + styles.item} type="submit">
                                <div className={styles["logout-icon"]} />
                                <div className={styles.title}>Logout</div>
                            </button>
                        </Form>
                    </>}
                </div>
            </div>
        </div>
    );
}

function Item({ target, children, keepMenuOpen }: { target: string, children: React.ReactNode, keepMenuOpen?: boolean }) {
    const location = useLocation();
    const navigation = useNavigation();
    const pathname = navigation.location?.pathname ?? location.pathname;
    const isSelected = target === "/" ? pathname === "/" : pathname.startsWith(target);
    const classes = [styles.item, isSelected ? styles.selected : null];
    return <>
        <Link {...className(classes)} to={target} data-keep-menu-open={keepMenuOpen ? "true" : undefined}>
            {children}
        </Link>
    </>;
}

function SubNav({ parent, items }: { parent: string, items: [string, string][] }) {
    const location = useLocation();
    const navigation = useNavigation();
    const pathname = navigation.location?.pathname ?? location.pathname;
    const search = navigation.location?.search ?? location.search;
    if (!pathname.startsWith(parent)) return null;

    const current = `${pathname}${search}`;
    return (
        <div className={styles.subnav}>
            {items.map(([target, label]) => {
                const isSelected = current === target
                    || (target.endsWith("=health") && current === "/health")
                    || (target.endsWith("=stats") && current === "/stats")
                    || (target.endsWith("=general") && current === "/settings");
                return <Link key={target} to={target} className={`${styles.subitem} ${isSelected ? styles["subitem-selected"] : ""}`}>{label}</Link>;
            })}
        </div>
    );
}
