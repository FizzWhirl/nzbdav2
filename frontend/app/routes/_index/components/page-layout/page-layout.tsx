import { useCallback, useEffect, useRef, useState } from "react";
import styles from "./page-layout.module.css";
import { useNavigation } from "react-router";

export type PageLayoutProps = {
    topNavComponent: (props: RequiredTopNavProps) => React.ReactNode,
    leftNavChild: React.ReactNode,
    bodyChild: React.ReactNode,
}

export type RequiredTopNavProps = {
    isHamburgerMenuOpen: boolean,
    onHamburgerMenuClick: () => void,
}

export function PageLayout(props: PageLayoutProps) {
    // data
    const [isHamburgerMenuOpen, setIsHamburgerMenuOpen] = useState(false);
    const isNavigating = Boolean(useNavigation().location);
    const keepMenuOpenAfterNavigation = useRef(false);

    // close hamburger-menu when done navigating
    useEffect(() => {
        if (isNavigating) return;
        if (keepMenuOpenAfterNavigation.current) {
            keepMenuOpenAfterNavigation.current = false;
            return;
        }
        setIsHamburgerMenuOpen(false);
    }, [isNavigating, setIsHamburgerMenuOpen]);

    // events
    const onHamburgerMenuClick = useCallback(function () {
        setIsHamburgerMenuOpen(!isHamburgerMenuOpen)
    }, [setIsHamburgerMenuOpen, isHamburgerMenuOpen]);

    const onBodyClick = useCallback(function () {
        setIsHamburgerMenuOpen(false);
    }, [setIsHamburgerMenuOpen]);

    const onLeftNavigationClick = useCallback(function (event: React.MouseEvent<HTMLDivElement>) {
        keepMenuOpenAfterNavigation.current = Boolean(
            (event.target as HTMLElement).closest('[data-keep-menu-open="true"]')
        );
    }, []);

    let containerClassName = styles["container"];
    if (isHamburgerMenuOpen) containerClassName += " " + styles["hamburger-open"];

    return (
        <>
            <div className={containerClassName}>
                <div className={styles["top-navigation"]}>
                    <props.topNavComponent
                        isHamburgerMenuOpen={isHamburgerMenuOpen}
                        onHamburgerMenuClick={onHamburgerMenuClick} />
                </div>
                <div className={styles["page"]}>
                    <div className={styles["left-navigation"]} onClick={onLeftNavigationClick}>
                        {props.leftNavChild}
                    </div>
                    {isHamburgerMenuOpen && <button
                        type="button"
                        aria-label="Close navigation menu"
                        className={styles.backdrop}
                        onClick={onBodyClick}
                    />}
                    <div className={styles["body"]} onClick={onBodyClick}>
                        {props.bodyChild}
                    </div>
                </div>
            </div>
        </>
    );
}